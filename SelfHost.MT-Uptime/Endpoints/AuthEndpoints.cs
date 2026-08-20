using System.Net;
using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MT.Uptime.Core.Notifications;
using MT.Uptime.Web.Security;
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

    /// <summary>
    /// Rate-limit policy on <c>/auth/login</c> (configured in Program.cs). Sign-in is anonymous and each
    /// attempt costs a PBKDF2 verification, so without a cap it is both a free password oracle and a
    /// cheap way to pin the CPU of a one-vCPU box.
    /// </summary>
    public const string LoginRateLimitPolicy = "login";

    /// <summary>
    /// Claim carrying <see cref="AppUser.SessionStamp"/>. Not a standard claim type, so it is namespaced
    /// to avoid colliding with anything the framework issues.
    /// </summary>
    public const string SessionStampClaim = "mt-uptime:session-stamp";

    private const int MinPasswordLength = 8;
    private const int MinUsernameLength = 3;

    /// <summary>
    /// Log category for sign-in outcomes. A fixed name rather than a generic type, so an operator can
    /// filter for it (<c>journalctl -u mt-uptime | grep MT.Uptime.Auth</c>) without knowing which class
    /// happens to host the endpoint.
    /// </summary>
    private const string AuthLogCategory = "MT.Uptime.Auth";

    /// <summary>
    /// The client address for a sign-in log line, taken from the connection after
    /// <c>UseForwardedHeaders</c> has resolved it — so behind the documented nginx setup this is the real
    /// caller, and behind nothing it is the real caller too.
    /// </summary>
    private static string ClientOf(HttpContext http)
    {
        // Deliberately the connection's address rather than a header. UseForwardedHeaders has already
        // run and has already replaced this with the forwarded client address *when the immediate peer
        // is a trusted proxy*; taking X-Real-IP or X-Forwarded-For directly, as this used to, meant
        // reading a value any caller can set. The result was a log where every client address is
        // attacker-chosen — and worse, the antiforgery-failure branch logs before any credential check,
        // so one cookie-less POST forged a line. An operator who then points fail2ban at this field has
        // handed out a remote ban primitive, including against themselves.
        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Renders an untrusted value for a log line: printable characters only, and length-capped.
    /// <para>
    /// The submitted username reaches the log on an anonymous, and until recently unthrottled, endpoint.
    /// Console and journald write it as bytes, so ANSI escapes are executed by whatever terminal the
    /// operator is tailing with — an attacker could emit cursor-up and erase-line sequences and scrub
    /// their own failed sign-ins off the screen, or forge a plausible success line with a bare CR.
    /// </para>
    /// </summary>
    private static string ForLog(string? value, int maxLength = 64)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var truncated = value.Length > maxLength ? value[..maxLength] : value;
        var clean = new StringBuilder(truncated.Length);
        foreach (var c in truncated)
            clean.Append(char.IsControl(c) ? '�' : c);

        return value.Length > maxLength ? clean.Append('…').ToString() : clean.ToString();
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
                    "or the password did not match", ForLog(username), ClientOf(http));
                return Results.Redirect($"/login?error=1{ReturnParam(returnUrl)}");
            }

            log.LogInformation("Signed in '{Username}' ({Role}) from {Client}",
                ForLog(user.Username), user.Role, ClientOf(http));

            await SignInAsync(http, user);
            return Results.LocalRedirect(SafeReturn(returnUrl));
        }).RequireRateLimiting(LoginRateLimitPolicy)
          .AllowAnonymous();   // signing in is by definition unauthenticated

        app.MapPost("/auth/logout", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            if (await ValidAsync(antiforgery, http))
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/login");
        }).AllowAnonymous();   // signing out must work even from an already-expired session

        app.MapPost("/auth/setup", async (
            HttpContext http, UserAccountService users, IAntiforgery antiforgery, SetupToken setupToken,
            ILoggerFactory loggers) =>
        {
            if (!await ValidAsync(antiforgery, http)) return Results.Redirect("/setup?error=1");
            if (await users.AnyUserExistsAsync()) return Results.LocalRedirect("/login");

            var form = await http.Request.ReadFormAsync();
            var username = form["username"].ToString().Trim();
            var email = form["email"].ToString().Trim();
            var password = form["password"].ToString();
            var confirm = form["confirm"].ToString();

            // Checked before the field validation below, so a wrong token cannot be told apart from a
            // wrong password by which error comes back — and logged, because an attempt here means
            // somebody found the wizard open.
            if (!setupToken.Validate(form["setupToken"].ToString()))
            {
                loggers.CreateLogger(AuthLogCategory).LogWarning(
                    "Setup attempt from {Client} rejected: the setup token did not match. The current " +
                    "token is in this log and at {Path}", ClientOf(http), setupToken.FilePath);
                return Results.Redirect("/setup?error=token");
            }

            if (username.Length < MinUsernameLength || password.Length < MinPasswordLength
                || password != confirm || !LooksLikeEmail(email))
                return Results.Redirect("/setup?error=1");

            // First run makes an Admin: there is nobody to promote them later.
            var user = await users.CreateAsync(username, password, email, UserRole.Admin);
            setupToken.Clear();   // single use: the wizard is now closed for good
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
            PublicUrl publicUrl, ILoggerFactory loggerFactory) =>
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

            // Deliberately not Request.Host — see PublicUrl. The recipient's browser goes wherever this
            // says, carrying a live token, so it must not be decided by the caller who asked for the reset.
            var link = $"{publicUrl.Origin(http.Request)}/reset-password?token={WebUtility.UrlEncode(token)}";
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
            // The role rides in the cookie, so nothing re-reads it per request. That would make a
            // demotion lag until the next sign-in, except that ChangeRoleAsync bumps the session stamp
            // below — which ends the session outright, so the stale claim never gets to be used.
            new(ClaimTypes.Role, user.Role.ToString()),
            // Pairs with UserAccountService.ValidateSessionAsync, called from the cookie handler's
            // OnValidatePrincipal on every request. This is what makes "Delete" and "Set password"
            // actually end the target's session rather than merely changing a row.
            new(SessionStampClaim, user.SessionStamp.ToString()),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>
    /// A return URL that is safe to hand to <see cref="Results.LocalRedirect"/>.
    /// <para>
    /// This paraphrases the framework's local-URL rule and must not drift from it: anything that passes
    /// here and then fails <c>LocalRedirect</c>'s own check throws <b>after</b> the auth cookie has been
    /// written, turning a correct sign-in into a 500 with no session. Blocking only <c>//</c> let
    /// <c>/\evil</c> through, which is exactly that case — a link an attacker can send to deny someone
    /// their own login.
    /// </para>
    /// <para>
    /// Allowlisted rather than blocklisted: the second character must be an ordinary path character, so
    /// backslashes, control characters and further slashes are all rejected without enumerating them.
    /// </para>
    /// </summary>
    private static string SafeReturn(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl[0] != '/') return "/";
        if (returnUrl.Length == 1) return returnUrl;              // "/" itself

        var second = returnUrl[1];
        if (second == '/' || second == '\\' || char.IsControl(second)) return "/";

        // A backslash or control character anywhere would also be rejected by LocalRedirect's parsing.
        return returnUrl.Any(c => c == '\\' || char.IsControl(c)) ? "/" : returnUrl;
    }

    private static string ReturnParam(string? returnUrl)
        => string.IsNullOrEmpty(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
}
