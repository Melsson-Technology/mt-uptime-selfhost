using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Core.Notifications;
using MT.Uptime.Core.Domain;
using MT.Uptime.Web.Security;
using MT.Uptime.Web.Services;

namespace MT.Uptime.Tests;

/// <summary>
/// Regressions for the security review of 2026-08-17. Each test pins one defect that was found by
/// reading the code and, in the first two cases, demonstrated against this same pipeline — so each one
/// fails if the fix is reverted. They drive the real middleware chain rather than calling handlers,
/// because every one of these bugs lived in the wiring rather than in a method.
/// </summary>
public class SessionRevocationTests
{
    /// <summary>
    /// The headline finding. Deleting an account left its cookie fully usable: the ticket is
    /// self-contained, nothing re-read the user row, and the deleted admin could still reach
    /// <c>/users</c> — and therefore create themselves a replacement account. "Delete" was cosmetic.
    /// </summary>
    [Fact]
    public async Task Deleting_an_account_stops_its_existing_session_working()
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var (client, cookie) = await AuthFlow.SignedInAsAsync(app, "doomed", UserRole.Editor);

        // Positive control first: the session works before the deletion, so a later 302 cannot be
        // explained by the sign-in having failed.
        Assert.Equal(HttpStatusCode.OK, (await AuthFlow.GetAsync(client, "/channels", cookie)).StatusCode);

        var users = app.Services.GetRequiredService<UserAccountService>();
        var target = (await users.ListAsync()).Single(u => u.Username == "doomed");
        var admin = (await users.ListAsync()).Single(u => u.Username == UptimeAppFactory.AdminUsername);
        Assert.Null(await users.DeleteUserAsync(target.Id, admin.Id));

        var after = await AuthFlow.GetAsync(client, "/channels", cookie);

        Assert.NotEqual(HttpStatusCode.OK, after.StatusCode);
    }

    /// <summary>
    /// The remedy an admin is offered for a compromised account is "Set password". It changed the hash
    /// and nothing else, so the attacker's existing cookie kept working — the one case where the victim
    /// believes they have acted and has not.
    /// </summary>
    [Fact]
    public async Task Setting_a_password_stops_existing_sessions_working()
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var (client, cookie) = await AuthFlow.SignedInAsAsync(app, "phished", UserRole.Editor);
        Assert.Equal(HttpStatusCode.OK, (await AuthFlow.GetAsync(client, "/channels", cookie)).StatusCode);

        var users = app.Services.GetRequiredService<UserAccountService>();
        var target = (await users.ListAsync()).Single(u => u.Username == "phished");
        Assert.Null(await users.SetPasswordAsync(target.Id, "a-brand-new-password"));

        var after = await AuthFlow.GetAsync(client, "/channels", cookie);

        Assert.NotEqual(HttpStatusCode.OK, after.StatusCode);
    }

    /// <summary>
    /// The role rides in the cookie, so a demotion used to bind only at the demoted user's next sign-in.
    /// Bumping the session stamp turns that into an immediate sign-out, which is the only version of
    /// "demote" that actually removes the privilege at the moment the admin clicks it.
    /// </summary>
    [Fact]
    public async Task Demoting_an_account_stops_its_existing_session_working()
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var (client, cookie) = await AuthFlow.SignedInAsAsync(app, "demoted", UserRole.Editor);
        Assert.Equal(HttpStatusCode.OK, (await AuthFlow.GetAsync(client, "/channels", cookie)).StatusCode);

        var users = app.Services.GetRequiredService<UserAccountService>();
        var target = (await users.ListAsync()).Single(u => u.Username == "demoted");
        Assert.Null(await users.ChangeRoleAsync(target.Id, UserRole.Viewer));

        var after = await AuthFlow.GetAsync(client, "/channels", cookie);

        Assert.NotEqual(HttpStatusCode.OK, after.StatusCode);
    }

    /// <summary>
    /// The control that stops the three above from passing for the wrong reason. Revalidation runs on
    /// every request, so a mistake there would lock everyone out of everything — which would look like
    /// security and be an outage.
    /// </summary>
    [Fact]
    public async Task An_untouched_session_keeps_working_across_requests()
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var (client, cookie) = await AuthFlow.SignedInAsAsync(app, "steady", UserRole.Editor);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await AuthFlow.GetAsync(client, "/channels", cookie)).StatusCode);
    }
}

/// <summary>
/// The first-run wizard mints an Admin and cannot require a login, so "the Users table is empty" was its
/// only authorization — a condition any passer-by can observe, because the first-run guard redirects
/// every page to <c>/setup</c>. A one-time token now has to be presented as well.
/// </summary>
public class SetupTokenTests
{
    [Fact]
    public async Task Setup_without_the_token_creates_no_account()
    {
        await using var app = new UptimeAppFactory();
        var client = app.CreateClient(NoRedirects);
        var (antiforgeryToken, cookie) = await AuthFlow.HarvestAntiforgeryAsync(client, "/setup");

        var response = await AuthFlow.PostFormAsync(client, "/auth/setup", cookie, new()
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["username"] = "attacker",
            ["email"] = "attacker@evil.example",
            ["password"] = "attacker-chosen-pass",
            ["confirm"] = "attacker-chosen-pass",
            // no setupToken field at all — the race the old code lost
        });

        Assert.Contains("error=token", response.Headers.Location?.ToString() ?? "");

        // The decisive check: a redirect could mislead, an account existing could not.
        var users = app.Services.GetRequiredService<UserAccountService>();
        Assert.False(await users.AnyUserExistsAsync());
    }

    [Fact]
    public async Task A_wrong_token_creates_no_account()
    {
        await using var app = new UptimeAppFactory();
        var client = app.CreateClient(NoRedirects);
        var (antiforgeryToken, cookie) = await AuthFlow.HarvestAntiforgeryAsync(client, "/setup");

        await AuthFlow.PostFormAsync(client, "/auth/setup", cookie, new()
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["setupToken"] = new string('a', 64),
            ["username"] = "attacker",
            ["email"] = "attacker@evil.example",
            ["password"] = "attacker-chosen-pass",
            ["confirm"] = "attacker-chosen-pass",
        });

        Assert.False(await app.Services.GetRequiredService<UserAccountService>().AnyUserExistsAsync());
    }

    /// <summary>The operator's path must still work, and the token must not survive its one use.</summary>
    [Fact]
    public async Task The_announced_token_completes_setup_and_is_then_destroyed()
    {
        await using var app = new UptimeAppFactory();
        var client = app.CreateClient(NoRedirects);

        var setup = app.Services.GetRequiredService<SetupToken>();
        var token = await File.ReadAllTextAsync(setup.FilePath);

        var (antiforgeryToken, cookie) = await AuthFlow.HarvestAntiforgeryAsync(client, "/setup");
        var response = await AuthFlow.PostFormAsync(client, "/auth/setup", cookie, new()
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["setupToken"] = token.Trim(),
            ["username"] = "operator",
            ["email"] = "operator@example.test",
            ["password"] = "the-real-operator-pass",
            ["confirm"] = "the-real-operator-pass",
        });

        Assert.DoesNotContain("error", response.Headers.Location?.ToString() ?? "");
        Assert.True(await app.Services.GetRequiredService<UserAccountService>().AnyUserExistsAsync());
        Assert.False(File.Exists(setup.FilePath));
    }

    private static WebApplicationFactoryClientOptions NoRedirects => new() { AllowAutoRedirect = false };
}

/// <summary>
/// A push monitor's ping URL is a bearer credential: anyone holding it can record an Up beat with no
/// session, pinning the monitor healthy and suppressing the alert it exists to send. The monitor detail
/// page is only <c>[Authorize]</c>, so it was handing that credential to read-only accounts.
/// </summary>
public class PushTokenVisibilityTests
{
    [Fact]
    public async Task A_viewer_is_not_shown_a_push_monitors_ping_token()
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var (id, token) = await app.SeedPushMonitorAsync();
        var (client, cookie) = await AuthFlow.SignedInAsAsync(app, "watcher", UserRole.Viewer);

        var html = await (await AuthFlow.GetAsync(client, $"/monitors/{id}", cookie)).Content.ReadAsStringAsync();

        Assert.DoesNotContain(token, html);
    }

    /// <summary>
    /// Positive control: without it the test above passes just as well if the panel broke for everyone,
    /// which would be an outage dressed as a fix.
    /// </summary>
    [Fact]
    public async Task An_editor_is_still_shown_the_ping_token()
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var (id, token) = await app.SeedPushMonitorAsync();
        var (client, cookie) = await AuthFlow.SignedInAsAsync(app, "maintainer", UserRole.Editor);

        var html = await (await AuthFlow.GetAsync(client, $"/monitors/{id}", cookie)).Content.ReadAsStringAsync();

        Assert.Contains(token, html);
    }

    /// <summary>
    /// The monitor export exists to leave the instance — copied to a laptop, attached to a ticket — so it
    /// must not carry credentials. It redacted only the database password, so it also shipped the push
    /// monitor's ping token in the clear: a bearer credential letting anyone forge that monitor's
    /// heartbeats and suppress its outage alerts.
    /// </summary>
    [Fact]
    public async Task The_monitor_export_carries_no_ping_token()
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var (_, token) = await app.SeedPushMonitorAsync();
        var (client, cookie) = await AuthFlow.SignedInAsAsync(app, "exporter", UserRole.Admin);

        var response = await AuthFlow.GetAsync(client, "/admin/export/monitors", cookie);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("nightly-backup", json);   // the export still describes the monitor
        Assert.DoesNotContain(token, json);        // but not its credential
    }
}

/// <summary>
/// The reset link used to be built from <c>Request.Host</c>. With AllowedHosts at its default of "*", an
/// unauthenticated caller could forge that header and have a genuine, correctly-signed reset email
/// delivered whose only link pointed at a host they own — carrying a live token.
/// </summary>
public class PasswordResetLinkTests
{
    /// <summary>
    /// The fix itself. AllowedHosts is deliberately left permissive here — several hostnames is a real
    /// configuration — so the forged Host reaches the handler and the assertion is about the link that
    /// gets built, not about who was let in.
    /// </summary>
    [Fact]
    public async Task The_reset_link_uses_the_configured_origin_not_the_request_host()
    {
        var mail = new CapturingEmailSender();
        await using var app = new UptimeAppFactory()
            .WithPublicBaseUrl("https://uptime.example.com")
            .WithAllowedHosts("uptime.example.com;localhost;uptime-example-com.attacker.tld")
            .WithEmailSender(mail);
        await app.SeedAdminAsync();

        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (antiforgeryToken, cookie) = await AuthFlow.HarvestAntiforgeryAsync(client, "/forgot-password");

        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/forgot")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = antiforgeryToken,
                ["email"] = UptimeAppFactory.AdminEmail,
            }),
        };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Host = "uptime-example-com.attacker.tld";
        await client.SendAsync(request);

        var body = Assert.Single(mail.Sent);
        Assert.Contains("https://uptime.example.com/reset-password?token=", body);
        Assert.DoesNotContain("attacker.tld", body);
    }

    /// <summary>
    /// Defence in depth: declaring the public URL also narrows AllowedHosts from its default of "*", so a
    /// forged Host never reaches a handler at all. This is what stops the same trick being used against
    /// any other Host-derived behaviour added later.
    /// </summary>
    [Fact]
    public async Task Declaring_the_public_url_makes_host_filtering_reject_a_forged_host()
    {
        var mail = new CapturingEmailSender();
        await using var app = new UptimeAppFactory()
            .WithPublicBaseUrl("https://uptime.example.com")
            .WithEmailSender(mail);
        await app.SeedAdminAsync();

        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/forgot")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = UptimeAppFactory.AdminEmail,
            }),
        };
        request.Headers.Host = "uptime-example-com.attacker.tld";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(mail.Sent);   // no token was ever issued, let alone mailed
    }

    /// <summary>
    /// …and narrowing must not lock out loopback, which is what every probe on the box itself uses.
    /// <para>
    /// Found on a real install. Setting <c>App:PublicBaseUrl</c> exactly as <c>mt-uptime.env.example</c>
    /// instructs narrowed AllowedHosts to the public hostname alone, so
    /// <c>http://127.0.0.1:5081/healthz</c> answered 400 — and the next deploy failed its own health
    /// check and rolled a perfectly good build back while the public site served 200 throughout. A
    /// hardening setting whose documented use breaks deployment does not survive contact with an
    /// operator; it gets removed, and the hardening goes with it.
    /// </para>
    /// <para>
    /// Nothing is conceded by allowing loopback. The forged Host this defends against arrives through
    /// the reverse proxy from the internet and cannot claim to be 127.0.0.1 — the proxy sets Host from
    /// the request line it received. Reaching Kestrel as loopback means already being on the machine.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public async Task Narrowing_host_filtering_still_admits_loopback(string loopbackHost)
    {
        await using var app = new UptimeAppFactory().WithPublicBaseUrl("https://uptime.example.com");
        await app.SeedAdminAsync();

        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Host = loopbackHost;

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<string> Sent { get; } = [];

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> SendAsync(string toEmail, string subject, string plainText, string html,
            CancellationToken ct = default)
        {
            Sent.Add(plainText);
            return Task.FromResult(true);
        }
    }
}

/// <summary>
/// Antiforgery harvesting and cookie-carrying requests, shared by the tests above. Deliberately the same
/// moves an attacker makes: obtaining a token pair is anonymous by design, so passing antiforgery must
/// never be sufficient on its own.
/// </summary>
internal static class AuthFlow
{
    internal static async Task<(HttpClient Client, string Cookie)> SignedInAsAsync(
        UptimeAppFactory app, string username, UserRole role)
    {
        const string password = "role-test-password";
        await app.SeedUserAsync(username, password, role);

        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var (token, antiforgery) = await HarvestAntiforgeryAsync(client, "/login");

        var response = await PostFormAsync(client, "/auth/login", antiforgery, new()
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = username,
            ["password"] = password,
        });

        var auth = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("; ", values.Select(v => v.Split(';')[0]))
            : "";
        Assert.Contains("MT-Uptime.Auth", auth);   // no cookie means the sign-in failed, not the policy

        return (client, $"{antiforgery}; {auth}");
    }

    internal static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    internal static async Task<(string Token, string Cookie)> HarvestAntiforgeryAsync(
        HttpClient client, string page)
    {
        var response = await client.GetAsync(page);
        var html = await response.Content.ReadAsStringAsync();

        var token = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("; ", values.Select(v => v.Split(';')[0]))
            : "";

        Assert.False(string.IsNullOrEmpty(token), $"no antiforgery token on {page} — the harvest step broke");
        return (token, cookie);
    }

    internal static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string path, string cookie, Dictionary<string, string> fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }
}

/// <summary>
/// Tier A of the security review: hardening that mostly guards against a future mistake rather than a
/// present hole, plus two genuine anonymous-reachability defects.
/// </summary>
public class HardeningTests
{
    /// <summary>
    /// MapRazorComponents needs AllowAnonymous so the login and status pages render, but that convention
    /// also reached the SignalR hub underneath — so an unauthenticated caller could negotiate a circuit
    /// and hold the socket open. Every anonymous page in this app is static SSR, so none needs one.
    /// </summary>
    [Fact]
    public async Task An_anonymous_caller_cannot_negotiate_a_blazor_circuit()
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", new StringContent(""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// blazor.web.js fetches this on every page load including anonymous ones, so gating the hub must
    /// not gate the initializers alongside it — that would break the login page for everyone.
    /// </summary>
    [Fact]
    public async Task The_blazor_initializers_endpoint_stays_anonymous()
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/_blazor/initializers");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Content-Security-Policy")]
    [InlineData("X-Content-Type-Options")]
    [InlineData("X-Frame-Options")]
    [InlineData("Referrer-Policy")]
    public async Task Security_headers_are_present_on_a_page_response(string header)
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/login");

        Assert.True(response.Headers.Contains(header), $"{header} was not sent");
    }

    /// <summary>
    /// The CSP is only worth having because no inline script remains. If one is reintroduced it will be
    /// silently blocked in the browser and nothing else here would notice.
    /// </summary>
    [Fact]
    public async Task The_policy_does_not_permit_inline_script()
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/login");
        var csp = string.Join("", response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
    }

    /// <summary>
    /// SafeReturn blocklisted only "//", so "/\evil" reached Results.LocalRedirect, which throws — after
    /// SignInAsync had already written the cookie. Correct credentials produced a 500 and no session,
    /// from a link an attacker can send.
    /// </summary>
    [Theory]
    [InlineData(@"/\evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\tevil")]
    public async Task A_crafted_return_url_neither_throws_nor_leaves_the_site(string returnUrl)
    {
        await using var app = new UptimeAppFactory();
        await app.SeedAdminAsync();
        var (client, cookie) = await AuthFlow.SignedInAsAsync(app, "returner", UserRole.Editor);

        var (token, antiforgery) = await AuthFlow.HarvestAntiforgeryAsync(client, "/login");
        var response = await AuthFlow.PostFormAsync(client, "/auth/login", antiforgery, new()
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = "returner",
            ["password"] = "role-test-password",
            ["returnUrl"] = returnUrl,
        });

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.DoesNotContain("evil.example", location);
    }
}

/// <summary>
/// The public status page is anonymous and costs a 30-day aggregation per monitor to build, so the
/// result is cached briefly. Exercised directly: the amplification it removes is invisible to a test
/// that only counts status codes.
/// </summary>
public class PublicStatusCacheTests
{
    [Fact]
    public async Task A_second_request_inside_the_window_does_not_rebuild()
    {
        var cache = new PublicStatusCache();
        var origin = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        var builds = 0;

        Task<string?> Build() { builds++; return Task.FromResult<string?>("built"); }

        Assert.Equal("built", await cache.GetOrBuildAsync("acme", Build, origin));
        Assert.Equal("built", await cache.GetOrBuildAsync("acme", Build, origin.AddSeconds(5)));

        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task The_entry_expires_so_a_real_outage_still_reaches_the_page()
    {
        var cache = new PublicStatusCache();
        var origin = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        var builds = 0;

        Task<string?> Build() { builds++; return Task.FromResult<string?>("built"); }

        await cache.GetOrBuildAsync("acme", Build, origin);
        await cache.GetOrBuildAsync("acme", Build, origin + PublicStatusCache.Lifetime.Add(TimeSpan.FromSeconds(1)));

        Assert.Equal(2, builds);
    }

    /// <summary>
    /// Slugs come from the URL, so an unknown one must be cached too — otherwise requesting a million
    /// distinct slugs bypasses the cache entirely, which is the case it exists to bound.
    /// </summary>
    [Fact]
    public async Task An_unknown_slug_is_cached_rather_than_rebuilt_every_time()
    {
        var cache = new PublicStatusCache();
        var origin = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        var builds = 0;

        Task<string?> Build() { builds++; return Task.FromResult<string?>(null); }

        Assert.Null(await cache.GetOrBuildAsync("nope", Build, origin));
        Assert.Null(await cache.GetOrBuildAsync("nope", Build, origin.AddSeconds(5)));

        Assert.Equal(1, builds);
    }
}

/// <summary>
/// Tier B: data arriving from a monitored target is outside the trust boundary. It is persisted on every
/// heartbeat, held in memory for the dashboard, and pasted into the outbound alert — so its size has to
/// be bounded at the point it enters, not wherever it happens to land.
/// </summary>
public class TargetControlledDataTests
{
    [Fact]
    public void A_huge_check_message_is_truncated_before_it_can_be_stored_or_alerted()
    {
        var hostile = new string('x', 64 * 1024);

        var down = CheckResult.Down(hostile);

        Assert.NotNull(down.Message);
        Assert.True(down.Message!.Length < CheckResult.MaxMessageLength + 64,
            $"message was {down.Message.Length} chars — the cap did not apply");
        Assert.EndsWith("(truncated)", down.Message);
    }

    [Fact]
    public void The_same_cap_applies_to_a_successful_check()
    {
        // Up carries the DNS answer summary, so it is target-controlled too.
        var result = CheckResult.Up(1.0, message: new string('y', 64 * 1024));

        Assert.True(result.Message!.Length < CheckResult.MaxMessageLength + 64);
    }

    [Fact]
    public void An_ordinary_message_is_left_exactly_as_it_is()
    {
        // The cap must not touch the normal case: these strings are what an operator reads at 03:00.
        const string ordinary = "Unexpected status 503";

        Assert.Equal(ordinary, CheckResult.Down(ordinary).Message);
        Assert.Equal(ordinary, CheckResult.Up(1.0, message: ordinary).Message);
    }

    /// <summary>
    /// A Down alert that exceeds a channel's payload limit is dropped by that channel — so an uncapped
    /// message hands the monitored party a way to suppress the alert about their own outage. This pins
    /// the resulting body well inside the smallest limit among the shipped channels (Telegram, 4096).
    /// </summary>
    [Fact]
    public void A_hostile_message_still_leaves_an_alert_small_enough_to_send()
    {
        var down = CheckResult.Down(new string('z', 200 * 1024));

        Assert.True(down.Message!.Length < 4096,
            "the capped message alone would exhaust Telegram's 4096-character payload limit");
    }
}

/// <summary>
/// A stored secret the key ring can no longer read is our fault, and the check has to say so. Both
/// database checkers used to swallow the decryption failure and fall back to a blank password, so the
/// probe completed and the Down reason an operator read at 03:00 was the target's own "Access denied
/// … (using password: NO)" — pointing at a database that was perfectly healthy. HttpChecker already
/// handled this correctly; these pin the backport to the other two.
/// </summary>
public class UnreadableCredentialTests
{
    private sealed class FailingProtector : MT.Uptime.Core.Security.ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) =>
            throw new System.Security.Cryptography.CryptographicException("key ring gone");
    }

    private sealed class PassthroughProtector : MT.Uptime.Core.Security.ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
    }

    private static MonitorContext ContextFor(MonitorType type, DbMonitorConfig cfg) => new(
        MonitorId: 1,
        Name: "probe-db",
        Type: type,
        Timeout: TimeSpan.FromSeconds(1),
        ConfigJson: JsonSerializer.Serialize(cfg));

    [Theory]
    [InlineData(MonitorType.MySql)]
    [InlineData(MonitorType.Postgres)]
    public async Task A_password_that_will_not_decrypt_is_a_hard_down_naming_the_key_ring(MonitorType type)
    {
        // Port 1 so that if the fix regressed and the probe went ahead with a blank password, the check
        // would fail with a connection error instead — a different message, and not hard.
        var cfg = new DbMonitorConfig { Host = "127.0.0.1", Port = 1, Username = "probe", Password = "ciphertext" };
        IMonitorChecker checker = type == MonitorType.MySql
            ? new MySqlChecker(new FailingProtector())
            : new PostgresChecker(new FailingProtector());

        var result = await checker.CheckAsync(ContextFor(type, cfg), CancellationToken.None);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.True(result.Hard, "a key ring cannot be brought back by retrying, so this must not burn the retry cushion");
        Assert.Contains("Data Protection", result.Message!);
    }

    /// <summary>
    /// The positive control. Reveal returns null and empty straight through, so a monitor with no stored
    /// password must still probe normally rather than reporting a key-ring failure it does not have.
    /// </summary>
    [Theory]
    [InlineData(MonitorType.MySql)]
    [InlineData(MonitorType.Postgres)]
    public async Task A_monitor_with_no_stored_password_still_probes(MonitorType type)
    {
        var cfg = new DbMonitorConfig { Host = "127.0.0.1", Port = 1, Username = "probe", Password = null };
        IMonitorChecker checker = type == MonitorType.MySql
            ? new MySqlChecker(new PassthroughProtector())
            : new PostgresChecker(new PassthroughProtector());

        var result = await checker.CheckAsync(ContextFor(type, cfg), CancellationToken.None);

        // Down either way — nothing is listening on port 1 — but for the target's reasons, not ours.
        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.DoesNotContain("Data Protection", result.Message!);
    }
}
