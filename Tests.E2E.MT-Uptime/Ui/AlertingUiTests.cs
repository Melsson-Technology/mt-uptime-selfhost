using Microsoft.Playwright;
using MT.Uptime.Core.Domain;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Ui;

/// <summary>
/// U2, U4, U5 and U10 — a channel configured through the form, a dashboard that moves on its own, an
/// incident acknowledged, and maintenance that suppresses the page.
/// <para>
/// These are the four scenarios where the browser is not an alternative to an API call but the only
/// way to reach the behaviour: the channel's "Send test" button, the live circuit push, the incident
/// acknowledgement, and the maintenance window form all live behind
/// <c>@rendermode InteractiveServer</c>.
/// </para>
/// </summary>
public class AlertingUiTests : IClassFixture<UiFixture>
{
    private readonly UiFixture _fx;

    public AlertingUiTests(UiFixture fx) => _fx = fx;

    [UIFact]
    public async Task U2_a_webhook_channel_configured_through_the_form_delivers_a_test_alert()
    {
        // The whole point of the "Send test" button is that an operator can prove a channel works
        // BEFORE an outage depends on it. That is worth testing precisely because the failure it
        // guards against — a channel that silently delivers nothing — is invisible from every screen.
        await _fx.StartAsync();
        var page = await _fx.SignInAsync();
        var sink = _fx.Sink;
        sink.Clear();

        var name = $"u2-{Guid.NewGuid():N}"[..14];

        await Forms.GotoInteractiveAsync(page, "/channels/new");
        await Forms.SelectAndConfirmAsync(page, "Type", "Webhook",
            page.GetByLabel("Target URL", new() { Exact = true }));
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync(name);

        // The URL field's label changes with the type — "Target URL" for a webhook, "Slack webhook
        // URL" for Slack. Asserting against the type-specific label is deliberate: it is what tells us
        // the type select actually re-rendered the form rather than merely changing a value.
        await page.GetByLabel("Target URL", new() { Exact = true }).FillAsync(sink.Url);
        await page.GetByLabel("Apply to all monitors (default channel)").CheckAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Send test" }).ClickAsync();

        // The test alert arrives before anything is saved — which is the behaviour, and the reason the
        // page shows an amber "tested but not saved" box rather than a green one.
        var alert = await sink.WaitForAsync(0, "Up", TimeSpan.FromSeconds(30));
        Assert.Equal("Up", alert.Kind);

        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(u => !u.Contains("/channels/new", StringComparison.Ordinal));
        // The row, not any text — same reason as CreateUserAsync's assertion. This page also shows a
        // confirmation message carrying the name, and matching that would pass whether or not the
        // channel was saved.
        await Assertions.Expect(page.Locator("tr", new() { HasText = name }).First).ToBeVisibleAsync();

        await DeleteChannelAsync(page, name);
    }

    [UIFact]
    public async Task U4_the_dashboard_flips_to_Down_without_a_reload()
    {
        // THE most valuable assertion in this tier, and the only one that can distinguish a working
        // Blazor circuit from a page that merely rendered once.
        //
        // MT-Uptime's dashboard claims to be live. If the circuit dies — a proxy that does not forward
        // WebSocket upgrades is the classic way, and the documented nginx config exists to prevent
        // exactly that — the page still loads, still shows monitors, and simply never changes again.
        // An operator watching a green dashboard through an outage is the worst failure this product
        // has, and nothing except a browser can detect it.
        await _fx.StartAsync();
        var page = await _fx.SignInAsync();
        var sink = _fx.Sink;

        var channel = $"u4-ch-{Guid.NewGuid():N}"[..14];
        await CreateWebhookChannelAsync(page, channel, sink.Url);

        var name = $"u4-{Guid.NewGuid():N}"[..14];
        await Forms.GotoInteractiveAsync(page, "/monitors/new");
        await MonitorForm.BeginAsync(page, MonitorType.Http, name);
        await page.GetByLabel("URL", new() { Exact = true }).FillAsync($"{Targets.HttpBaseUrl}/toggle");
        await MonitorForm.SaveAsync(page);

        await MonitorForm.WaitForStatusAsync(page, name, "Up");
        sink.Clear();

        using (var broken = TargetControl.Break(Target.Http))
        {
            // No ReloadAsync anywhere in this block. If the assertion below needed one, the feature
            // would be broken and the test would be hiding it.
            await MonitorForm.WaitForStatusAsync(page, name, "Down", timeoutMs: 30_000);

            await sink.WaitForAsync(name, "Down", TimeSpan.FromSeconds(30));

            broken.RestoreNow();

            await MonitorForm.WaitForStatusAsync(page, name, "Up", timeoutMs: 30_000);
        }

        await MonitorsUiTests.DeleteMonitorAsync(page, name);
        await DeleteChannelAsync(page, channel);
    }

    [UIFact]
    public async Task U5_an_incident_can_be_acknowledged_and_annotated()
    {
        // Note where the controls actually are, because the plan assumed otherwise: **Acknowledge is
        // a button in the row on /incidents**, not on the detail page. The detail page shows the
        // acknowledgement once it exists and carries the status-update form. Writing this against
        // /incidents/{id} looking for an Acknowledge button would have failed on the box.
        await _fx.StartAsync();
        var page = await _fx.SignInAsync();

        var name = $"u5-{Guid.NewGuid():N}"[..14];
        await Forms.GotoInteractiveAsync(page, "/monitors/new");
        await MonitorForm.BeginAsync(page, MonitorType.Http, name);
        await page.GetByLabel("URL", new() { Exact = true }).FillAsync($"{Targets.HttpBaseUrl}/toggle");
        await MonitorForm.SaveAsync(page);
        await MonitorForm.WaitForStatusAsync(page, name, "Up");

        var update = $"Investigating. Ref {Guid.NewGuid():N}"[..30];

        using (var broken = TargetControl.Break(Target.Http))
        {
            await MonitorForm.WaitForStatusAsync(page, name, "Down", timeoutMs: 30_000);

            await Forms.GotoInteractiveAsync(page, "/incidents");
            var row = page.Locator("tr", new() { HasText = name });
            await Assertions.Expect(row.First).ToBeVisibleAsync(new() { Timeout = 20_000 });

            await row.First.GetByRole(AriaRole.Button, new() { Name = "Acknowledge", Exact = true }).ClickAsync();

            // The State cell becomes "Acknowledged by <who>". Asserted on the row rather than
            // anywhere on the page, so a stray "Acknowledged" in the explanatory blurb at the top
            // cannot satisfy it — that text is on the page before anything is clicked.
            await Assertions.Expect(page.Locator("tr", new() { HasText = name }).First
                    .Locator("td[data-label='State']"))
                .ToContainTextAsync("Acknowledged", new() { Timeout = 15_000 });

            // The annotation half: a status update that a customer-facing status page would carry.
            await page.Locator("tr", new() { HasText = name }).First
                .GetByRole(AriaRole.Link).First.ClickAsync();

            await page.GetByLabel("Update", new() { Exact = true }).FillAsync(update);
            await page.GetByRole(AriaRole.Button, new() { Name = "Post update" }).ClickAsync();
            await Assertions.Expect(page.GetByText(update)).ToBeVisibleAsync(new() { Timeout = 15_000 });

            broken.RestoreNow();
        }

        await Forms.GotoInteractiveAsync(page, "/");
        await MonitorForm.WaitForStatusAsync(page, name, "Up", timeoutMs: 30_000);
        await MonitorsUiTests.DeleteMonitorAsync(page, name);
    }

    [UIFact]
    public async Task U10_an_active_maintenance_window_suppresses_the_page_but_not_the_recovery()
    {
        // The asymmetry is the whole feature and it is easy to get wrong in the safe-looking direction.
        //
        // Suppressing Down alerts during planned work is obviously right. Suppressing the RECOVERY
        // would mean a service that failed for real during a maintenance window is never announced as
        // fixed — an operator watching their alerts would have no idea anything had happened. So
        // recoveries are never suppressed, and this asserts both halves in one run.
        await _fx.StartAsync();
        var page = await _fx.SignInAsync();
        var sink = _fx.Sink;

        var channel = $"u10-ch-{Guid.NewGuid():N}"[..15];
        await CreateWebhookChannelAsync(page, channel, sink.Url);

        var name = $"u10-{Guid.NewGuid():N}"[..15];
        await Forms.GotoInteractiveAsync(page, "/monitors/new");
        await MonitorForm.BeginAsync(page, MonitorType.Http, name);
        await page.GetByLabel("URL", new() { Exact = true }).FillAsync($"{Targets.HttpBaseUrl}/toggle");
        await MonitorForm.SaveAsync(page);
        await MonitorForm.WaitForStatusAsync(page, name, "Up");

        var window = $"u10-win-{Guid.NewGuid():N}"[..16];
        await Forms.GotoInteractiveAsync(page, "/maintenance/new");
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync(window);

        // A one-off window spanning now. Written in the browser's local time because the input is a
        // datetime-local; the box runs UTC, which is what makes that safe here and worth saying out
        // loud rather than leaving as a coincidence.
        var now = DateTime.UtcNow;
        await page.GetByLabel("Starts (UTC)", new() { Exact = true })
            .FillAsync(now.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm"));
        await page.GetByLabel("Ends (UTC)", new() { Exact = true })
            .FillAsync(now.AddHours(1).ToString("yyyy-MM-ddTHH:mm"));

        // The per-monitor checkboxes only render when "Every monitor" is OFF — the picker is inside an
        // `@if (!AppliesToAllMonitors)`. It defaults to off, so the uncheck below is a no-op today and
        // is here so that a change to that default turns this into a still-passing test rather than a
        // confusing "checkbox not found".
        await page.GetByLabel("Every monitor, including ones added later").UncheckAsync();
        await page.GetByRole(AriaRole.Checkbox, new() { Name = name }).CheckAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(u => !u.Contains("/maintenance/new", StringComparison.Ordinal));

        sink.Clear();

        using (var broken = TargetControl.Break(Target.Http))
        {
            // The monitor still goes Down — suppression is about the ALERT, not about the state. A
            // maintenance window that hid the outage from the dashboard too would be actively
            // dangerous.
            await Forms.GotoInteractiveAsync(page, "/");
            await MonitorForm.WaitForStatusAsync(page, name, "Down", timeoutMs: 30_000);

            // Long enough that a delivery which was merely slow would have arrived.
            await sink.AssertNoneAsync(name, "Down", TimeSpan.FromSeconds(15));

            broken.RestoreNow();

            // And the recovery still pages, window or no window.
            await sink.WaitForAsync(name, "Up", TimeSpan.FromSeconds(45));
        }

        await DeleteMaintenanceWindowAsync(page, window);
        await MonitorsUiTests.DeleteMonitorAsync(page, name);
        await DeleteChannelAsync(page, channel);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static async Task CreateWebhookChannelAsync(IPage page, string name, string url)
    {
        await Forms.GotoInteractiveAsync(page, "/channels/new");
        await Forms.SelectAndConfirmAsync(page, "Type", "Webhook",
            page.GetByLabel("Target URL", new() { Exact = true }));
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync(name);
        await page.GetByLabel("Target URL", new() { Exact = true }).FillAsync(url);
        await page.GetByLabel("Apply to all monitors (default channel)").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(u => !u.Contains("/channels/new", StringComparison.Ordinal));
    }

    private static async Task DeleteChannelAsync(IPage page, string name)
    {
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

        await Forms.GotoInteractiveAsync(page, "/channels");
        var row = page.Locator("tr", new() { HasText = name });
        if (await row.CountAsync() == 0) return;

        await row.First.GetByRole(AriaRole.Link).First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(u => !u.Contains("/edit", StringComparison.Ordinal));
    }

    private static async Task DeleteMaintenanceWindowAsync(IPage page, string name)
    {
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

        await Forms.GotoInteractiveAsync(page, "/maintenance");
        var row = page.Locator("tr", new() { HasText = name });
        if (await row.CountAsync() == 0) return;

        await row.First.GetByRole(AriaRole.Link).First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(u => !u.Contains("/edit", StringComparison.Ordinal));
    }
}
