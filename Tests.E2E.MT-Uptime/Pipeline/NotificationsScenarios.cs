using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Pipeline;

/// <summary>
/// S11 and S12 — which channels fire, and how failures on one host become one incident rather than
/// twenty alerts.
/// <para>
/// These two share a file because they are the same question from opposite ends: routing. One decides
/// which channel an event reaches; the other decides how many events there are to route.
/// </para>
/// <para>
/// This class does <b>not</b> use the shared fixture's default channel. Every test here seeds its own
/// channels, because the thing under test is the selection rule itself.
/// </para>
/// </summary>
public class NotificationsScenarios : IAsyncLifetime
{
    private E2EAppFactory _app = null!;
    private WebhookSink _defaultSink = null!;
    private WebhookSink _linkedSink = null!;

    // IAsyncLifetime rather than a class fixture, and deliberately: InitializeAsync runs AFTER xUnit
    // has honoured Skip, so on a machine with no manifest none of this executes. A class fixture's
    // constructor runs before that decision, which is the trap Support/E2EAppFactory documents.
    public async Task InitializeAsync()
    {
        if (!Targets.Available) return;

        _app = new E2EAppFactory();
        _defaultSink = new WebhookSink();
        _linkedSink = new WebhookSink();

        await _app.EnsureStartedAsync();
        await _app.SeedAdminAsync();
    }

    public Task DisposeAsync()
    {
        _app?.Dispose();
        _defaultSink?.Dispose();
        _linkedSink?.Dispose();
        return Task.CompletedTask;
    }

    [E2EFact]
    public async Task A_default_channel_fires_for_every_monitor_and_a_linked_one_only_for_its_own()
    {
        // The selection rule is a single EF predicate — Enabled && (IsDefault || linked to this
        // monitor) — and getting it wrong in either direction is quiet and bad. Too broad and every
        // team is paged about every other team's service; too narrow and a channel somebody
        // configured never fires and nobody finds out until an outage goes unannounced.
        var watched = await _app.SeedMonitorAsync(
            "s11-watched",
            MonitorType.Http,
            Probe.Json(new HttpMonitorConfig { Url = $"{Targets.HttpBaseUrl}/toggle" }));

        var other = await _app.SeedMonitorAsync(
            "s11-other",
            MonitorType.Tcp,
            Probe.Json(new TcpMonitorConfig { Host = Targets.Host, Port = Targets.TcpRefusedPort }));

        await _app.SeedWebhookChannelAsync(_defaultSink.Url, isDefault: true, name: "s11-default");
        await _app.SeedWebhookChannelAsync(
            _linkedSink.Url, isDefault: false, name: "s11-linked", monitorIds: [watched]);

        await _app.WaitForStatusAsync(watched, [MonitorStatus.Up], TimeSpan.FromSeconds(45));

        // 'other' points at a closed port, so it is Down from its first check — a second monitor
        // alerting for free, which is what makes the negative assertion below meaningful.
        await _app.WaitForStatusAsync(other, [MonitorStatus.Down], TimeSpan.FromSeconds(45));

        // The default channel hears about the monitor it is not linked to.
        await _defaultSink.WaitForAsync(other, "Down", TimeSpan.FromSeconds(45));

        // The linked one must not. Its monitor has not failed, and it is not a default.
        await _linkedSink.AssertNoneAsync(other, "Down", TimeSpan.FromSeconds(3));

        using (var broken = TargetControl.Break(Target.Http))
        {
            // Now break the one it IS linked to: both channels get it.
            await _linkedSink.WaitForAsync(watched, "Down", TimeSpan.FromSeconds(45));
            await _defaultSink.WaitForAsync(watched, "Down", TimeSpan.FromSeconds(10));

            broken.RestoreNow();
        }

        await _linkedSink.WaitForAsync(watched, "Up", TimeSpan.FromSeconds(45));
    }

    [E2EFact]
    public async Task A_channel_URL_stored_as_plaintext_delivers_nothing_and_says_so_only_in_the_log()
    {
        // DOCUMENTS A TRAP, and one worth knowing about before it is met in production.
        //
        // WebhookNotificationChannel calls Reveal on the stored URL. A value that is not decryptable
        // ciphertext comes back null, SendAsync returns false, and the dispatcher logs a warning —
        // and that is the entire user-visible consequence. Nothing on the channel page says the
        // channel is broken, and "Send test" fails the same silent way.
        //
        // Anything that writes a channel row without going through ISecretProtector — a hand-run SQL
        // insert, a restore of a database without its key ring, an import written in a hurry —
        // produces a monitoring system whose alerts go nowhere while every screen looks healthy.
        var monitorId = await _app.SeedMonitorAsync(
            "s11-plaintext-url",
            MonitorType.Tcp,
            Probe.Json(new TcpMonitorConfig { Host = Targets.Host, Port = Targets.TcpRefusedPort }));

        // protect: false is the whole test — the same URL, stored the way a careless writer would.
        await _app.SeedWebhookChannelAsync(
            _defaultSink.Url, isDefault: true, name: "s11-plaintext", protect: false);

        await _app.WaitForStatusAsync(monitorId, [MonitorStatus.Down], TimeSpan.FromSeconds(45));

        // The monitor went Down and an incident opened, so the pipeline is working; only the delivery
        // is missing. That asymmetry is what makes this dangerous rather than obvious.
        Assert.Single(await _app.IncidentsAsync(monitorId));
        await _defaultSink.AssertNoneAsync(monitorId, "Down", TimeSpan.FromSeconds(10));
    }

    [E2EFact]
    public async Task Two_monitors_failing_on_one_host_open_a_single_correlated_incident()
    {
        // S12. The feature that stops a box falling over from producing an alert per service on it.
        // CorrelationKeyResolver keys HTTP, TCP and database monitors by the address they depend on;
        // failures sharing a key inside the window join one incident instead of opening several.
        //
        // On this box every host is 127.0.0.1, which makes correlation trivially easy to demonstrate
        // and is also why the whole assembly runs serially — see AssemblyInfo. Two scenarios failing
        // concurrently would see each other's monitors here.
        await _app.SeedWebhookChannelAsync(_defaultSink.Url, isDefault: true, name: "s12-default");

        var first = await _app.SeedMonitorAsync(
            "s12-http-a",
            MonitorType.Http,
            Probe.Json(new HttpMonitorConfig { Url = $"{Targets.HttpBaseUrl}/toggle" }));

        var second = await _app.SeedMonitorAsync(
            "s12-http-b",
            MonitorType.Http,
            Probe.Json(new HttpMonitorConfig { Url = $"{Targets.HttpBaseUrl}/toggle" }));

        await _app.WaitForStatusAsync(first, [MonitorStatus.Up], TimeSpan.FromSeconds(45));
        await _app.WaitForStatusAsync(second, [MonitorStatus.Up], TimeSpan.FromSeconds(45));
        _defaultSink.Clear();

        using (var broken = TargetControl.Break(Target.Http))
        {
            // One break takes both down: they share a route, so this is the real shape of the problem
            // rather than a contrived one.
            await _defaultSink.WaitForAsync(first, "Down", TimeSpan.FromSeconds(45));
            await _defaultSink.WaitForAsync(second, "Down", TimeSpan.FromSeconds(45));

            var firstIncidents = await _app.IncidentsAsync(first);
            var secondIncidents = await _app.IncidentsAsync(second);

            var incident = Assert.Single(firstIncidents);
            Assert.Equal(incident.Id, Assert.Single(secondIncidents).Id);

            // Not merely shared — counted. MonitorCount is what the alert renders as "and 1 other
            // service", and an incident joined without it being incremented would read as a single
            // service failing.
            Assert.Equal(2, incident.MonitorCount);
            Assert.False(string.IsNullOrWhiteSpace(incident.CorrelationKey));

            broken.RestoreNow();
        }

        await _defaultSink.WaitForAsync(first, "Up", TimeSpan.FromSeconds(45));
        await _defaultSink.WaitForAsync(second, "Up", TimeSpan.FromSeconds(45));
    }
}
