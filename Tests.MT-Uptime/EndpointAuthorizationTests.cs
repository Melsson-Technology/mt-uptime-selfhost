using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MT.Uptime.Core.Domain;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Core.Notifications;
using MT.Uptime.Web.Services;

namespace MT.Uptime.Tests;

/// <summary>
/// Which endpoints an ANONYMOUS caller may reach, verified against the real middleware pipeline.
/// <para>
/// This exists because of a live defect: <c>/auth/profile</c> and <c>/auth/password</c> shipped without
/// <c>.RequireAuthorization()</c>. Since <c>/login</c> hands an antiforgery token to anonymous visitors,
/// anyone could mint a valid token pair, POST to <c>/auth/profile</c> to move the admin's email to an
/// address they controlled, then use "forgot password" to take the account over — two requests, no
/// credentials. Endpoint authorization cannot be unit-tested; it only exists in the pipeline, so these
/// boot the real app.
/// </para>
/// </summary>
public class EndpointAuthorizationTests : IClassFixture<UptimeAppFactory>
{
    private readonly UptimeAppFactory _app;

    public EndpointAuthorizationTests(UptimeAppFactory app) => _app = app;

    /// <summary>Follows no redirects — a 302 to /login is a *pass*, and following it would hide that.</summary>
    private HttpClient NewClient() => _app.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    /// <summary>
    /// There must be an account to attack. Without one the app is in first-run mode, every page redirects
    /// to the setup wizard, and the takeover tests would pass for the wrong reason — blocked because no
    /// user existed, rather than because authorization rejected them.
    /// </summary>
    private Task SeedAdminAsync() => _app.SeedAdminAsync();

    // --- The endpoints that must never be anonymous ----------------------------------------------

    [Theory]
    [InlineData("/auth/profile")]
    [InlineData("/auth/password")]
    public async Task Account_mutating_endpoints_reject_an_anonymous_caller(string path)
    {
        await SeedAdminAsync();
        var client = NewClient();
        var (token, cookie) = await HarvestAntiforgeryAsync(client);

        var response = await PostFormAsync(client, path, cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = "admin",
            ["email"] = "attacker@evil.example",
            ["current"] = "whatever",
            ["password"] = "attacker-chosen-password",
            ["confirm"] = "attacker-chosen-password",
        });

        // Unauthorized, or bounced to the login page. What it must NOT be is a redirect back to
        // /profile?saved=…, which would mean the write went through.
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"{path} answered {(int)response.StatusCode} to an anonymous caller");
        Assert.DoesNotContain("saved=", response.Headers.Location?.ToString() ?? "");

        // The decisive check: the account is untouched. A status code can mislead; the stored email
        // moving to an address the attacker controls is the actual takeover.
        Assert.Equal(UptimeAppFactory.AdminEmail, await _app.CurrentAdminEmailAsync());
    }

    [Fact]
    public async Task Anonymous_callers_cannot_reach_the_admin_data_endpoints()
    {
        var client = NewClient();

        foreach (var path in new[] { "/admin/backup", "/admin/export/monitors" })
        {
            var response = await client.GetAsync(path);
            Assert.True(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    or HttpStatusCode.Redirect or HttpStatusCode.Found,
                $"{path} answered {(int)response.StatusCode} to an anonymous caller");
        }
    }

    // --- The endpoints that must STAY anonymous --------------------------------------------------

    [Theory]
    [InlineData("/healthz")]          // load balancers and uptime checks reach this unauthenticated
    [InlineData("/login")]
    [InlineData("/forgot-password")]
    public async Task Public_endpoints_stay_reachable_without_credentials(string path)
    {
        var response = await NewClient().GetAsync(path);

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"{path} answered {(int)response.StatusCode} to an anonymous caller");
    }

    [Fact]
    public async Task The_push_ping_endpoint_stays_anonymous()
    {
        // The token in the URL is the only credential — monitored cron jobs have no session. An unknown
        // token must 404 (rejected on its merits), never 401 (rejected before it was even looked up).
        var response = await NewClient().GetAsync("/ping/00000000000000000000000000000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Role gates: authenticated is not the same as authorized ---------------------------------

    [Theory]
    [InlineData("/admin/backup")]
    [InlineData("/admin/export/monitors")]
    public async Task A_signed_in_viewer_is_refused_the_admin_data_endpoints(string path)
    {
        // The anonymous test above cannot catch a regression here: dropping the policy back to a bare
        // .RequireAuthorization() still rejects anonymous callers, so that test would stay green while
        // every Viewer on the instance gained the ability to download the whole database.
        await SeedAdminAsync();
        var (client, cookie) = await SignedInAsAsync("watcher", UserRole.Viewer);

        var response = await GetWithCookieAsync(client, path, cookie);

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"{path} answered {(int)response.StatusCode} to a signed-in Viewer");

        // Refused, not asked to log in again: they already are, so /login would send them straight back.
        Assert.DoesNotContain("/login", response.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task An_admin_can_still_reach_the_admin_data_endpoints()
    {
        // Positive control. Without it the test above passes just as well if the endpoints are broken
        // for everyone, which would look like security and be an outage.
        await SeedAdminAsync();
        var (client, cookie) = await SignedInAsync(UptimeAppFactory.AdminUsername, UptimeAppFactory.AdminPassword);

        var response = await GetWithCookieAsync(client, "/admin/export/monitors", cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Signing_in_issues_the_role_as_a_claim()
    {
        // The policies are role-based, so a sign-in that forgets the claim locks everyone out of
        // everything above Viewer — including the only admin.
        await SeedAdminAsync();
        var (client, cookie) = await SignedInAsAsync("editorial", UserRole.Editor);

        // /channels is Editor-gated; reaching it proves the claim survived the round trip into the cookie.
        var response = await GetWithCookieAsync(client, "/channels", cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Helpers ---------------------------------------------------------------------------------

    /// <summary>Creates an account with the given role and returns a client holding its auth cookie.</summary>
    private async Task<(HttpClient Client, string Cookie)> SignedInAsAsync(string username, UserRole role)
    {
        const string password = "role-test-password";
        await _app.SeedUserAsync(username, password, role);
        return await SignedInAsync(username, password);
    }

    private async Task<(HttpClient Client, string Cookie)> SignedInAsync(string username, string password)
    {
        var client = NewClient();
        var (token, antiforgery) = await HarvestAntiforgeryAsync(client);

        var response = await PostFormAsync(client, "/auth/login", antiforgery, new Dictionary<string, string>
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

    private static Task<HttpResponseMessage> GetWithCookieAsync(HttpClient client, string path, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    /// <summary>
    /// Pulls an antiforgery token and its paired cookie off the login page — exactly what an attacker
    /// would do, which is the point: passing antiforgery must not be sufficient to mutate an account.
    /// </summary>
    private static async Task<(string Token, string Cookie)> HarvestAntiforgeryAsync(HttpClient client)
    {
        var response = await client.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // if this 302s, the admin was not seeded
        var html = await response.Content.ReadAsStringAsync();

        var token = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("; ", values.Select(v => v.Split(';')[0]))
            : "";

        Assert.False(string.IsNullOrEmpty(token), "no antiforgery token on /login — the harvest step broke");
        return (token, cookie);
    }

    private static Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string path, string cookie, Dictionary<string, string> fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }
}

/// <summary>
/// Boots the real application against a throwaway database and key ring, so tests never touch the
/// developer's <c>App_Data</c>. The monitoring engine starts as it would in production, but a fresh
/// database has no monitors, so nothing is probed.
/// </summary>
public sealed class UptimeAppFactory : WebApplicationFactory<Program>, IDisposable
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "seeded-test-password";
    public const string AdminEmail = "admin@example.test";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"mt-uptime-itest-{Guid.NewGuid():N}");

    /// <summary>Creates the admin account if absent, so the app is past first-run setup. Idempotent.</summary>
    public async Task SeedAdminAsync()
    {
        var users = Services.GetRequiredService<UserAccountService>();
        if (!await users.AnyUserExistsAsync())
            await users.CreateAsync(AdminUsername, AdminPassword, AdminEmail, UserRole.Admin);
    }

    /// <summary>Creates an account with a specific role, if it does not already exist. Idempotent.</summary>
    public async Task SeedUserAsync(string username, string password, UserRole role)
    {
        var users = Services.GetRequiredService<UserAccountService>();
        if ((await users.ListAsync()).Any(u => u.Username == username)) return;
        await users.CreateAsync(username, password, email: null, role);
    }

    /// <summary>The stored admin email — the value an account-takeover attempt would try to move.</summary>
    public async Task<string?> CurrentAdminEmailAsync()
        => (await Services.GetRequiredService<UserAccountService>().GetAsync())?.Email;

    /// <summary>
    /// Creates a push monitor and returns its id and ping token, so a test can assert on who is shown
    /// that token. Written straight to the database rather than through the editor, which is Editor-gated
    /// and would beg the question.
    /// </summary>
    public async Task<(int Id, string Token)> SeedPushMonitorAsync()
    {
        var token = PushMonitorManager.NewToken();
        await using var db = await Services
            .GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();

        var monitor = new Monitor
        {
            Name = "nightly-backup",
            Type = MonitorType.Push,
            Enabled = true,
            ConfigJson = JsonSerializer.Serialize(new PushMonitorConfig { Token = token, GraceSeconds = 30 }),
        };
        db.Monitors.Add(monitor);
        await db.SaveChangesAsync();
        return (monitor.Id, token);
    }

    private string? _publicBaseUrl;
    private string? _allowedHosts;
    private IEmailSender? _emailSender;

    /// <summary>
    /// Sets AllowedHosts explicitly. Declaring a public base URL narrows it automatically, so a test that
    /// needs a forged Host to reach a handler has to opt out of that narrowing.
    /// </summary>
    public UptimeAppFactory WithAllowedHosts(string hosts)
    {
        _allowedHosts = hosts;
        return this;
    }

    /// <summary>Declares the instance's public origin, as a real deployment does via App__PublicBaseUrl.</summary>
    public UptimeAppFactory WithPublicBaseUrl(string url)
    {
        _publicBaseUrl = url;
        return this;
    }

    /// <summary>Captures outbound mail so a test can read the link that was actually sent.</summary>
    public UptimeAppFactory WithEmailSender(IEmailSender sender)
    {
        _emailSender = sender;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_root);
        builder.UseSetting("Storage:DatabasePath", Path.Combine(_root, "test.db"));
        builder.UseSetting("Storage:DataProtectionKeysPath", Path.Combine(_root, "keys"));
        if (_publicBaseUrl is not null) builder.UseSetting("App:PublicBaseUrl", _publicBaseUrl);
        if (_allowedHosts is not null) builder.UseSetting("AllowedHosts", _allowedHosts);
        builder.UseEnvironment(Environments.Production);

        if (_emailSender is not null)
            builder.ConfigureServices(s => s.AddSingleton(_emailSender));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}
