using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Web.Components;
using MT.Uptime.Web.Endpoints;
using MT.Uptime.Web.Security;
using MT.Uptime.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Run cleanly as a systemd service when present (journald logging + Type=notify readiness); no-op elsewhere.
builder.Host.UseSystemd();

// --- Storage paths (relative paths resolve under the content root; overridden to /var/lib/mt-uptime in prod) ---
var contentRoot = builder.Environment.ContentRootPath;
string ResolvePath(string p) => Path.IsPathRooted(p) ? p : Path.Combine(contentRoot, p);

var dbPath = ResolvePath(builder.Configuration["Storage:DatabasePath"] ?? "App_Data/mt-uptime.db");
var keysPath = ResolvePath(builder.Configuration["Storage:DataProtectionKeysPath"] ?? "App_Data/keys");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
Directory.CreateDirectory(keysPath);

// --- Data Protection: persist keys so auth cookies and encrypted secrets survive restarts ---
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("MT-Uptime");

// --- EF Core (SQLite) via a thread-safe context factory ---
var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(connectionString)
           .AddInterceptors(new SqlitePragmaInterceptor()));
builder.Services.AddSingleton<DatabaseInitializer>();

// --- Monitoring engine + notifications ---
builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection("Engine"));
builder.Services.AddMonitoringEngine();

// --- Authentication (single admin, cookie) ---
builder.Services.AddSingleton<IPasswordHasher<AppUser>>(new PasswordHasher<AppUser>());
builder.Services.AddSingleton<UserAccountService>();
// Beside the database, so it inherits the state directory's 0700 in the documented deployments.
builder.Services.AddSingleton(sp => new SetupToken(
    Path.GetDirectoryName(dbPath)!, sp.GetRequiredService<ILogger<SetupToken>>()));

// --- The origin used in emailed links ------------------------------------------------------------
var publicUrl = new PublicUrl(
    builder.Configuration["App:PublicBaseUrl"],
    LoggerFactory.Create(b => b.AddConfiguration(builder.Configuration.GetSection("Logging")).AddConsole())
        .CreateLogger<PublicUrl>());
builder.Services.AddSingleton(publicUrl);
builder.Services.AddSingleton<PublicStatusCache>();

// Declaring the public URL also tightens host filtering, which otherwise defaults to "*" and accepts any
// Host header. Only narrowed when the operator has not set AllowedHosts themselves, so an explicit value
// (several hostnames, a wildcard domain) is never overridden.
if (publicUrl.ConfiguredHost is { } declaredHost
    && string.IsNullOrWhiteSpace(builder.Configuration["AllowedHosts"]) is false
    && builder.Configuration["AllowedHosts"] == "*")
{
    // Loopback is always kept alongside the declared host, and that is not a concession — it is the
    // difference between this setting being usable and being a trap.
    //
    // Narrowing to the public hostname alone rejects Host: 127.0.0.1 with a 400, which is what every
    // local probe sends: deploy-on-server.sh health-checks http://127.0.0.1:5081/healthz, and so does
    // anything else an operator runs on the box. Observed on a real install: setting App:PublicBaseUrl
    // exactly as mt-uptime.env.example instructs made the next deploy fail its health check and roll
    // itself back, while the public site served 200 throughout. A setting whose documented use breaks
    // deployment is worse than the hardening it buys.
    //
    // Nothing is given away. The header this defends against — H5, a forged Host used to build a
    // password-reset link — arrives through the reverse proxy from the internet, and cannot claim to be
    // loopback: the proxy sets Host from the request line it received. Reaching Kestrel with
    // Host: localhost means already being on the host.
    builder.Configuration["AllowedHosts"] = $"{declaredHost};localhost;127.0.0.1;[::1]";
}
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.LogoutPath = "/logout";
        // Not /login: this fires when the caller IS signed in but lacks the role, and asking them to
        // re-enter credentials that were never the problem just loops them back here.
        o.AccessDeniedPath = "/denied";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
        o.Cookie.Name = "MT-Uptime.Auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // Secure when the request is HTTPS. Behind Nginx, UseForwardedHeaders makes IsHttps true.
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        // The ticket is self-contained and encrypted under a key ring that outlives restarts, so without
        // this nothing on the server can revoke it. Deleting an account or setting its password changed a
        // row and left the holder's cookie working — including well enough to visit /users and mint a
        // replacement account, which made the deletion cosmetic. Re-check every request instead.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var principal = ctx.Principal;
            var idClaim = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var stampClaim = principal?.FindFirst(AuthEndpoints.SessionStampClaim)?.Value;

            // A cookie with no stamp claim predates this check, so it cannot be validated and is refused.
            // That signs out anyone holding a session issued by the previous build — the intended cost of
            // closing the hole, and the reason this is worth landing while the user table is small.
            if (int.TryParse(idClaim, out var userId) && int.TryParse(stampClaim, out var stamp))
            {
                var users = ctx.HttpContext.RequestServices.GetRequiredService<UserAccountService>();
                if (await users.ValidateSessionAsync(userId, stamp, ctx.HttpContext.RequestAborted))
                    return;
            }

            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        };
    });
// Authenticated by default. An endpoint that forgets to declare a policy gets the safe one rather than
// becoming public — which is exactly how /auth/profile shipped reachable by anonymous callers. Genuinely
// public endpoints opt out explicitly with .AllowAnonymous() or [AllowAnonymous], so the exceptions are
// visible and reviewable instead of being the silent default.
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // Roles are cumulative, so each policy names every role at or above it rather than just its own.
    // Spelled out per policy on purpose: an ordering trick ("role >= Editor") reads well but puts the
    // privilege comparison in one clever place, where adding a role later silently widens every gate.
    o.AddPolicy(AuthPolicies.Editor, p => p.RequireRole(
        nameof(UserRole.Editor), nameof(UserRole.Admin)));
    o.AddPolicy(AuthPolicies.Admin, p => p.RequireRole(
        nameof(UserRole.Admin)));
});
builder.Services.AddCascadingAuthenticationState();

// --- Rate limiting for /ping/{token} -------------------------------------------------------------
// The ping route is the only anonymous *write* path on the box: a valid token enqueues work onto the
// single-writer heartbeat channel. Tokens are 128-bit random, so guessing is infeasible; this caps
// flooding (from a leaked token or a bad cron loop) and bounds lookup cost from any one source.
// Partitioned by client IP — UseForwardedHeaders runs first, so behind Nginx that is the real caller.
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy(PushEndpoints.RateLimitPolicy, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                // Generous for legitimate use: a push monitor pings once per interval, and many
                // monitors may share one NAT egress IP. Still cuts a flood by orders of magnitude.
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Sign-in is anonymous, and each attempt that names a real account costs a PBKDF2 verification —
    // tens of milliseconds of CPU on a box sized for one vCPU. Uncapped that is two problems at once:
    // unlimited offline-speed password guessing, and a cheap way for an anonymous caller to starve the
    // monitoring runners of the thread pool they share. 20 in 5 minutes leaves an operator who fat-fingers
    // their password several tries, and reduces guessing to a rate no wordlist survives.
    o.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
            }));

    // Password reset is anonymous and sends mail, so it is throttled far harder than the ping route:
    // it guards against both reset-link spam to a real inbox and blind token submission.
    o.AddPolicy(AuthEndpoints.ResetRateLimitPolicy, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
            }));

    // Write a plain-text body: pingers are machines, and a body also stops
    // UseStatusCodePagesWithReExecute from re-rendering the HTML "not found" page over a 429.
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            ctx.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        await ctx.HttpContext.Response.WriteAsync("Too many requests. Try again later.", ct);
    };
});

builder.Services.AddHealthChecks();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Must follow AddInteractiveServerComponents, which registers the plain ServerAuthenticationStateProvider
// this replaces. Without it, OnValidatePrincipal would revoke sessions on HTTP requests only, and an open
// interactive circuit — which is where every mutating page lives — would keep working after the account
// was deleted or demoted.
builder.Services.AddScoped<AuthenticationStateProvider, RevalidatingUserAuthenticationState>();

// Behind the Nginx reverse proxy: trust X-Forwarded-For/Proto so scheme + secure cookies behave.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // The framework's defaults trust loopback and nothing else, which is precisely the documented
    // deployment: Kestrel binds 127.0.0.1 and nginx is the only possible sender. They are therefore
    // left alone.
    //
    // They used to be cleared, which does not mean "no proxies" — it means *trust these headers from
    // anyone*. That is correct behind the documented proxy and wrong everywhere else, and the shipped
    // docker-compose published the port straight onto the host, so a caller could set their own
    // X-Forwarded-For. Both rate limiters partition on the resulting address, so rotating the header
    // gave every request its own partition and no cap applied at all.
    //
    // A proxy that is not on loopback — nginx in another container, a load balancer — has to be
    // declared, because there is no way to tell its forwarded headers from a client's:
    //     ForwardedHeaders__KnownProxies=203.0.113.7,172.18.0.0/16
    var declared = builder.Configuration["ForwardedHeaders:KnownProxies"] ?? "";
    foreach (var entry in declared.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (IPAddress.TryParse(entry, out var ip)) o.KnownProxies.Add(ip);
        else if (System.Net.IPNetwork.TryParse(entry, out var network)) o.KnownIPNetworks.Add(network);
        else throw new InvalidOperationException(
            $"ForwardedHeaders:KnownProxies contains '{entry}', which is neither an IP address nor a " +
            "CIDR network. Refusing to start rather than silently trusting nothing and rate-limiting " +
            "every caller into one bucket.");
    }
});

var app = builder.Build();

app.UseForwardedHeaders();

// Create/upgrade the database (PRAGMAs + migrations) before serving any traffic.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();

    // Then decide whether the setup wizard is open, and if so announce its token — before the first
    // request can be served, so there is no window in which /setup is reachable without one.
    var accounts = scope.ServiceProvider.GetRequiredService<UserAccountService>();
    await app.Services.GetRequiredService<SetupToken>().EnsureAsync(await accounts.AnyUserExistsAsync());
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// Security response headers. None of these fixes a known hole in this app — they are the layer that
// decides how much a *future* mistake costs, and they are the first thing any scanner or drive-by
// reviewer checks, so their absence generates reports whether or not anything is wrong.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;

    // script-src 'self' is the directive that earns its keep: it means an HTML injection anywhere in
    // the app cannot become script execution. It is only affordable because the four inline onclick
    // handlers moved into wwwroot/js/copy-field.js — do not reintroduce inline script.
    //
    // style-src keeps 'unsafe-inline' deliberately. Tag chips colour themselves through a style
    // attribute, and Blazor's own reconnect UI injects inline styles; the colour is already validated
    // to #RRGGBB before it is interpolated, which is where that risk is actually managed.
    //
    // connect-src includes ws:/wss: for the Blazor circuit's WebSocket. frame-ancestors 'none' is the
    // clickjacking control that actually applies to modern browsers; X-Frame-Options is its fallback.
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' ws: wss:; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "object-src 'none'";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    // Status pages are public and may be linked from anywhere; sending the full URL of an admin page
    // to a third-party host in a Referer would leak monitor ids and page structure for free.
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=(), interest-cohort=()";

    await next();
});

// Endpoint-scoped: only routes with .RequireRateLimiting are affected. Routing is registered ahead of
// user middleware by WebApplication, so the endpoint's policy metadata is already resolved here.
app.UseRateLimiter();

app.UseAuthentication();

// First-run guard: until an admin account exists, funnel every page to the setup wizard.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var allow = path.StartsWithSegments("/_framework")
        || path.StartsWithSegments("/_blazor")
        || path.StartsWithSegments("/setup")
        || path.StartsWithSegments("/auth")
        || path.StartsWithSegments("/ping")
        || path.StartsWithSegments("/healthz")
        || (path.Value?.Contains('.') ?? false); // static assets
    if (!allow)
    {
        var users = context.RequestServices.GetRequiredService<UserAccountService>();
        if (!await users.AnyUserExistsAsync(context.RequestAborted))
        {
            context.Response.Redirect("/setup");
            return;
        }
    }
    await next();
});

// Gate the Blazor circuit. MapRazorComponents(...).AllowAnonymous() is required — the anonymous pages
// (login, setup, password reset, public status) have to render — but that convention also reaches the
// SignalR hub endpoints underneath, so an unauthenticated caller could POST /_blazor/negotiate, complete
// the WebSocket handshake and hold the connection open indefinitely without ever starting a circuit.
// Every anonymous page in this app is static SSR, so none of them needs a circuit at all.
//
// /_blazor/initializers is exempt: blazor.web.js fetches it on every page load, anonymous ones included.
// Must sit after UseAuthentication, or context.User is not populated yet.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/_blazor")
        && !path.StartsWithSegments("/_blazor/initializers")
        && context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        // Write a body for the same reason the rate limiter does: UseStatusCodePagesWithReExecute only
        // re-runs the pipeline for a bodiless response, and re-executing a POST as /not-found trips
        // antiforgery and rewrites this 401 into a 400 — a misleading answer to a caller that is being
        // refused for authentication, and a confusing one to whoever reads the logs.
        await context.Response.WriteAsync("Unauthorized.");
        return;
    }
    await next();
});

app.UseAuthorization();

// Must precede UseAntiforgery: it drops cookies encrypted under a key ring this instance no longer has,
// which would otherwise surface as a bare 400 on the first form a new install submits.
app.UseStaleAntiforgeryCookieRecovery();
app.UseAntiforgery();

// Each .AllowAnonymous() below is a deliberate exception to the authenticated-by-default fallback policy.
app.MapStaticAssets().AllowAnonymous();                  // css/js/images are needed to render the login page
app.MapHealthChecks("/healthz").AllowAnonymous();        // load balancers and external uptime checks have no session
app.MapAuthEndpoints();                                  // opts out per-route; see AuthEndpoints
app.MapAdminEndpoints(dbPath);                           // already .RequireAuthorization() on both routes
app.MapPushEndpoints();                                  // the ping token is the credential; see PushEndpoints
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    // Page access is enforced by each component's own [Authorize]/[AllowAnonymous] attribute via
    // AuthorizeRouteView. The fallback policy must not also gate the component endpoints, or the
    // anonymous pages (login, setup, password reset, public status pages) and the Blazor circuit
    // negotiate become unreachable.
    .AllowAnonymous();

app.Run();

/// <summary>
/// Top-level statements generate an internal Program class; this makes it public so the test project can
/// boot the real pipeline through <c>WebApplicationFactory&lt;Program&gt;</c>. Endpoint authorization can
/// only be verified against the actual middleware chain, not by unit-testing a handler.
/// </summary>
public partial class Program;
