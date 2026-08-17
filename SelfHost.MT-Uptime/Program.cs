using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
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

// Behind the Nginx reverse proxy: trust X-Forwarded-For/Proto so scheme + secure cookies behave.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Kestrel listens only on 127.0.0.1, so Nginx is the sole possible source of these headers.
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

// Create/upgrade the database (PRAGMAs + migrations) before serving any traffic.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

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
