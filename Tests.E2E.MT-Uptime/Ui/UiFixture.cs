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

    /// <summary>
    /// A fresh browser context, signed in, on the dashboard.
    /// <para>
    /// A context per test rather than a page per test: contexts are cheap, they carry their own cookie
    /// jar, and sharing one would mean a test that changed the signed-in user — which
    /// <c>UsersUiTests</c> does — leaked that into whatever ran next.
    /// </para>
    /// </summary>
    public async Task<IPage> SignInAsync(string? username = null, string? password = null)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1400, Height = 1000 },
        });

        var page = await context.NewPageAsync();

        // Generous, and it has to be: the first navigation of a run pays for Blazor's bundle and the
        // circuit handshake, and this box is also running MySQL, PostgreSQL, nginx, dnsmasq and the
        // application under test.
        page.SetDefaultTimeout(30_000);

        await page.GotoAsync("/login");
        await page.GetByLabel("Username").FillAsync(username ?? Targets.AdminUser!);
        await page.GetByLabel("Password").FillAsync(password ?? Targets.AdminPassword!);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        await page.WaitForURLAsync(u => !u.Contains("/login", StringComparison.Ordinal));
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
