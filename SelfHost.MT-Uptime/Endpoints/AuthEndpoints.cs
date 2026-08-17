using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MT.Uptime.Core.Notifications;
using MT.Uptime.Web.Services;

namespace MT.Uptime.Web.Endpoints;

/// <summary>
/// Cookie sign-in/out, profile edits, and the password-reset flow. All of these either write response
/// headers (SignInAsync) or need a real form post, neither of which can happen inside the Blazor
/// interactive circuit — so the matching pages are static SSR and post here.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Rate-limit policy guarding the anonymous reset endpoints (configured in Program.cs).</summary>
    public const string ResetRateLimitPolicy = "password-reset";

    private const int MinPasswordLength = 8;
    private const int MinUsernameLength = 3;

    /// <summary>
    /// Log category for sign-in outcomes. A fixed name rather than a generic type, so an operator can
    /// filter for it (<c>journalctl -u mt-uptime | grep MT.Uptime.Auth</c>) without knowing which class
    /// happens to host the endpoint.
    /// </summary>
    private const string AuthLogCategory = "MT.Uptime.Auth";

    /// <summary>
    /// The client address for a sign-in log line. Prefers the proxy's <c>X-Real-IP</c>, because this
    /// deployment sits behind nginx and the socket address is otherwise always 127.0.0.1 — which would
    /// make every log line identical and useless for spotting a password-spray.
    /// </summary>
    private static string ClientOf(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Real-IP"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded)) return forwarded;

        var chain = http.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(chain)) return chain.Split(',')[0].Trim();

        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", async (
            HttpContext http, UserAccountService users, IAntiforgery antiforgery, ILoggerFactory loggers) =>
        {
            var log = loggers.CreateLogger(AuthLogCategory);

            // The browser is deliberately told the same thing whichever of these fails — revealing which
            // half was wrong is a gift to anyone guessing. The server log is where they are told apart,
            // and without it there is no way to answer "why can I not sign in": a stale antiforgery
            // cookie, an unknown username and a wrong password are indistinguishable from the outside.
            if (!await ValidAsync(antiforgery, http))
            {
                log.LogWarning("Sign-in rejected for {Client}: antiforgery validation failed (usually a " +
                    "stale or missing cookie — clearing site cookies normally fixes it)", ClientOf(http));
                return Results.Redirect("/login?error=1");
            }

            var form = await http.Request.ReadFormAsync();
            var username = form["username"].ToString().Trim();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            var user = await users.VerifyAsync(username, password);
            if (user is null)
            {
                // The submitted username is logged; the password never is, not even its length.
                log.LogWarning("Sign-in failed for username '{Username}' from {Client}: no such account, " +
                    "or the password did not match", username, ClientOf(http));
                return Results.Redirect($"/login?error=1{ReturnParam(returnUrl)}");
            }

            log.LogInformation("Signed in '{Username}' ({Role}) from {Client}",
                user.Username, user.Role, ClientOf(http));

            await SignInAsync(http, user);
            return Results.LocalRedirect(SafeReturn(returnUrl));
        }).AllowAnonymous();   // signing in is by definition unauthenticated

        app.MapPost("/auth/logout", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            if (await ValidAsync(antiforgery, http))
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/login");
        }).AllowAnonymous();   // signing out must work even from an already-expired session

        app.MapPost("/auth/setup", async (HttpContext http, UserAccountService users, IAntiforgery antiforgery) =>
        {
            if (!await ValidAsync(antiforgery, http)) return Results.Redirect("/setup?error=1");
            if (await users.AnyUserExistsAsync()) return Results.LocalRedirect("/login");

            var form = await http.Request.ReadFormAsync();
            var username = form["username"].ToString().Trim();
            var email = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            var confirm = form["confirm"].ToString();

            if (username.Length < MinUsernameLength || password.Length < MinPasswordLength
                || password != confirm || !LooksLikeEmail(email))
                return Results.Redirect("/setup?error=1");

            // First run makes an Admin: there is nobody to promote them later.
            var user = await users.CreateAsync(username, password, email, UserRole.Admin);
            await SignInAsync(http, user);
            return Results.LocalRedirect("/");
        }).AllowAnonymous();   // first-run setup happens before any account exists

        // --- Profile -----------------------------------------------------------------------------

        app.MapPost("/auth/profile", async (HttpContext http, UserAccountService users, IAntiforgery antiforgery) =>
        {
            if (!await ValidAsync(antiforgery, http)) return Results.Redirect(ProfileError("Please try again."));

            var user = await CurrentUserAsync(http, users);
            if (user is null) return Results.LocalRedirect("/login");

            var form = await http.Request.ReadFormAsync();
            var username = form["username"].ToString().Trim();
            var displayName = form["displayName"].ToString();
            var email = form["email"].ToString().Trim();

            if (username.Length < MinUsernameLength)
                return Results.Redirect(ProfileError($"Username must be at least {MinUsernameLength} characters."));
            if (email.Length > 0 && !LooksLikeEmail(email))
                return Results.Redirect(ProfileError("That email address doesn't look valid."));

            var error = await users.UpdateProfileAsync(user.Id, username, displayName, email);
            if (error is not null) return Results.Redirect(ProfileError(error));

            // The auth cookie carries the username as its Name claim, so a rename would otherwise leave a
            // stale name in the header until the next sign-in. Re-issue it with the current values.
            var updated = await users.GetByIdAsync(user.Id);
            if (updated is not null) await SignInAsync(http, updated);

            return Results.LocalRedirect("/profile?saved=profile");
        }).RequireAuthorization();

        app.MapPost("/auth/password", async (HttpContext http, UserAccountService users, IAntiforgery antiforgery) =>
        {
            if (!await ValidAsync(antiforgery, http)) return Results.Redirect(ProfileError("Please try again."));

            var user = await CurrentUserAsync(http, users);
            if (user is null) return Results.LocalRedirect("/login");

            var form = await http.Request.ReadFormAsync();
            var current = form["current"].ToString();
            var password = form["password"].ToString();
            var confirm = form["confirm"].ToString();

            if (password.Length < MinPasswordLength)
                return Results.Redirect(ProfileError($"New password must be at least {MinPasswordLength} characters."));
            if (password != confirm)
                return Results.Redirect(ProfileError("The new passwords don't match."));

            var error = await users.ChangePasswordAsync(user.Id, current, password);
            if (error is not null) return Results.Redirect(ProfileError(error));

            return Results.LocalRedirect("/profile?saved=password");
        }).RequireAuthorization();

        // --- Password reset ----------------------------------------------------------------------

        app.MapPost("/auth/forgot", async (
            HttpContext http, UserAccountService users, IEmailSender email, IAntiforgery antiforgery,
            ILoggerFactory loggerFactory) =>
        {
            // Every path below returns the same redirect. Whether the address exists, whether SendGrid is
            // configured, and whether delivery succeeded must all be indistinguishable from outside, or
            // this endpoint becomes a way to enumerate accounts.
            var done = Results.LocalRedirect("/forgot-password?sent=1");
            if (!await ValidAsync(antiforgery, http)) return done;

            var form = await http.Request.ReadFormAsync();
            var address = form["email"].ToString().Trim();

            var token = await users.BeginPasswordResetAsync(address);
            if (token is null) return done;

            var link = $"{http.Request.Scheme}://{http.Request.Host}/reset-password?token={WebUtility.UrlEncode(token)}";
            var hours = (int)UserAccountService.ResetTokenLifetime.TotalHours;
            var sent = await email.SendAsync(
                address,
                "[MT-Uptime] Reset your password",
                $"""
                 Someone asked to reset the password for your MT-Uptime account.

                 Open this link to choose a new one (valid for {hours} hour(s)):
                 {link}

                 If this wasn't you, ignore this email — your password has not changed.
                 """,
                $"""
                 <p>Someone asked to reset the password for your MT-Uptime account.</p>
                 <p><a href="{WebUtility.HtmlEncode(link)}">Choose a new password</a> (valid for {hours} hour(s)).</p>
                 <p>If this wasn't you, ignore this email — your password has not changed.</p>
                 """);

            if (!sent)
            {
                // Log loudly: from the browser this is indistinguishable from success, so the server log is
                // the only place an operator can discover that resets are not actually deliverable.
                loggerFactory.CreateLogger("MT.Uptime.Auth").LogError(
                    "Password reset requested but the email could not be sent. Check the SendGrid settings; "
                    + "the account cannot be recovered by email until delivery works.");
            }

            return done;
        }).RequireRateLimiting(ResetRateLimitPolicy)
          .AllowAnonymous();   // the whole point is recovering access without a session

        app.MapPost("/auth/reset", async (HttpContext http, UserAccountService users, IAntiforgery antiforgery) =>
        {
            var form = await http.Request.ReadFormAsync();
            var token = form["token"].ToString();
            var password = form["password"].ToString();
            var confirm = form["confirm"].ToString();

            string Fail(string message)
                => $"/reset-password?token={WebUtility.UrlEncode(token)}&error={WebUtility.UrlEncode(message)}";

            if (!await ValidAsync(antiforgery, http)) return Results.Redirect(Fail("Please try again."));
            if (password.Length < MinPasswordLength)
                return Results.Redirect(Fail($"Password must be at least {MinPasswordLength} characters."));
            if (password != confirm)
                return Results.Redirect(Fail("The passwords don't match."));

            var error = await users.CompletePasswordResetAsync(token, password);
            if (error is not null) return Results.Redirect(Fail(error));

            // Sign in explicitly rather than auto-authenticating from the token: proving you can read the
            // mailbox is enough to set a password, and the new password is then what grants the session.
            return Results.LocalRedirect("/login?reset=1");
        }).RequireRateLimiting(ResetRateLimitPolicy)
          .AllowAnonymous();   // the whole point is recovering access without a session
    }

    private static string ProfileError(string message) => $"/profile?error={WebUtility.UrlEncode(message)}";

    /// <summary>
    /// The account making the request, resolved from the principal's <see cref="ClaimTypes.NameIdentifier"/>
    /// claim. Endpoints must never act on "the first user row" — with one account the two are the same,
    /// but the difference is a privilege-escalation bug as soon as there is a second.
    /// </summary>
    private static async Task<AppUser?> CurrentUserAsync(HttpContext http, UserAccountService users)
        => int.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
            ? await users.GetByIdAsync(id)
            : null;

    /// <summary>
    /// A deliberately loose sanity check, not RFC validation — the address only has to be plausible here.
    /// Whether it actually receives mail is proven by the reset email arriving, which no regex can decide.
    /// </summary>
    private static bool LooksLikeEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320) return false;
        var at = value.IndexOf('@');
        return at > 0
            && at == value.LastIndexOf('@')
            && at < value.Length - 1
            && !value.Contains(' ')
            && value.LastIndexOf('.') > at;
    }

    private static async Task<bool> ValidAsync(IAntiforgery antiforgery, HttpContext http)
    {
        try { await antiforgery.ValidateRequestAsync(http); return true; }
        catch { return false; }
    }

    private static async Task SignInAsync(HttpContext http, AppUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName is { Length: > 0 } d ? d : user.Username),
            // The role rides in the cookie, so a role change does not take effect until the affected user
            // signs in again (or an admin's edit re-issues it). That is the standard cookie-auth trade and
            // is acceptable here — but it means a *demotion* is not immediate, which UserAccountService's
            // guardrails assume nothing about: they re-check the database on every write.
            new(ClaimTypes.Role, user.Role.ToString()),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    private static string SafeReturn(string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
            ? returnUrl
            : "/";

    private static string ReturnParam(string? returnUrl)
        => string.IsNullOrEmpty(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
}
