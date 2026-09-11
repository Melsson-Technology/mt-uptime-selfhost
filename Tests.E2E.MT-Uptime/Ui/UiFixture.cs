using Microsoft.Playwright;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Ui;

/// <summary>
/// A headless Chromium signed in to the installed MT-Uptime, and a webhook sink its alerts can reach.
/// <para>
/// Tier 3 is the only tier that drives the <b>installed instance</b> — the one `smoke.sh` completed
/// setup on, served through nginx on port 80 — rather than an in-process host. That is the point:
/// every configuring page in this application is <c>@rendermode InteractiveServer</c>, so a Blazor
/// circuit over a WebSocket is the only way any of it can be exercised, and none of it had ever been
/// driven by a browser.
/// </para>
/// <para>
/// <b>Nothing happens in the constructor</b>, for the third time and the same reason: xUnit builds a
/// class fixture before it honours <c>Skip</c>, so launching Chromium here would download and start a
/// browser on every laptop that has no manifest. <see cref="StartAsync"/> is what a running test calls.
/// </para>
/// </summary>
public sealed class UiFixture : IAsyncDisposable, IDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private WebhookSink? _sink;

    public IBrowser Browser => _browser
        ?? throw new InvalidOperationException("call StartAsync() first — the fixture constructor does no work on purpose");

    public WebhookSink Sink => _sink
        ?? throw new InvalidOperationException("call StartAsync() first — the fixture constructor does no work on purpose");

    /// <summary>The installed instance's origin, as smoke.sh recorded it. Through nginx, not the app port.</summary>
    public string BaseUrl => Targets.BaseUrl!.TrimEnd('/');

    public async Task StartAsync()
    {
        if (_browser is not null) return;

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // The box is a t3.medium and Chromium's default /dev/shm is small on a cloud image; without
            // this the renderer dies part-way through a run with a crash that reads like a test failure.
            Args = ["--disable-dev-shm-usage", "--no-sandbox"],
        });
        _sink = new WebhookSink();
    }

    /// <summary>The administrator's cookie jar, captured from the one real sign-in. See <see cref="SignInAsync"/>.</summary>
    private string? _adminStorageState;

    /// <summary>
    /// A fresh browser context, signed in, on the dashboard.
    /// <para>
    /// A context per test rather than a page per test: contexts are cheap, they carry their own cookie
    /// jar, and sharing one would mean a test that changed the signed-in user — which
    /// <c>UsersUiTests</c> does — leaked that into whatever ran next. That property is unchanged by
    /// everything below: every caller still gets its own context.
    /// </para>
    /// <para>
    /// <b>What changed is the number of real sign-ins, and it had to.</b> The login endpoint permits
    /// <b>20 attempts per 5 minutes partitioned by client address</b>, and that address is the same for
    /// every test: nginx forwards it and <c>UseForwardedHeaders</c> resolves it back to the box's own
    /// loopback. This tier had <b>fourteen</b> administrator sign-ins across its call sites plus two
    /// deliberate ones as other users — sixteen of twenty, inside a run that finishes well within one
    /// window. It passed on luck: whether the fixed window happened to roll mid-run. A nineteenth test
    /// tipped it over on 2026-09-10, and the tier failed <em>inside this method</em> with a navigation
    /// timeout that reads like a broken page rather than a spent budget.
    /// </para>
    /// <para>
    /// The limit is not the thing to change. It is what makes offline-speed password guessing
    /// impractical and what stops an anonymous caller starving the monitoring runners of the PBKDF2 CPU
    /// they share with every checker. So the administrator signs in <b>once</b>, the resulting cookie
    /// jar is captured with <c>StorageStateAsync</c>, and later contexts are seeded from it — sixteen
    /// attempts become three. A caller naming a <em>different</em> user still signs in for real,
    /// because that is the thing those tests are testing.
    /// </para>
    /// <para>
    /// <b>The seeded session is verified, not assumed.</b> An earlier attempt at this cached the state
    /// and trusted it, and when tests failed it was impossible to tell a stale cookie from a broken
    /// page — because an unauthenticated context does not error, it quietly redirects to
    /// <c>/login</c> and every later locator times out somewhere unrelated. If the replayed jar no
    /// longer authenticates, this falls back to a real sign-in and re-captures. The cost of being
    /// wrong is one extra permit; the cost of not checking was a day.
    /// </para>
    /// </summary>
    public async Task<IPage> SignInAsync(string? username = null, string? password = null)
    {
        var asAdmin = username is null && password is null;

        if (asAdmin && _adminStorageState is not null)
        {
            var seeded = await NewPageAsync(_adminStorageState);
            await seeded.GotoAsync("/");

            // Landing anywhere but /login means the cookie still authenticates, and this is where a
            // real sign-in would have left us: the post-login redirect is the dashboard.
            if (!seeded.Url.Contains("/login", StringComparison.Ordinal)) return seeded;

            // Stale. Drop it and pay for a real sign-in rather than hand back an anonymous context.
            _adminStorageState = null;
            await seeded.Context.CloseAsync();
        }

        var page = await NewPageAsync(storageState: null);

        await page.GotoAsync("/login");
        await page.GetByLabel("Username").FillAsync(username ?? Targets.AdminUser!);
        await page.GetByLabel("Password").FillAsync(password ?? Targets.AdminPassword!);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        await page.WaitForURLAsync(u => !u.Contains("/login", StringComparison.Ordinal));

        if (asAdmin) _adminStorageState = await page.Context.StorageStateAsync();
        return page;
    }

    /// <summary>
    /// A context and a page, optionally seeded with a saved cookie jar.
    /// <para>
    /// The timeout is generous and has to be: the first navigation of a run pays for Blazor's bundle
    /// and the circuit handshake, and this box is also running MySQL, PostgreSQL, nginx, dnsmasq and
    /// the application under test.
    /// </para>
    /// </summary>
    private async Task<IPage> NewPageAsync(string? storageState)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 1000 },
            StorageState = storageState,
        });

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30_000);
        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
        _sink?.Dispose();
    }

    // xUnit 2.x disposes a class fixture through IDisposable; IAsyncDisposable alone is not enough, so
    // both are implemented and the synchronous one drives the asynchronous one.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
