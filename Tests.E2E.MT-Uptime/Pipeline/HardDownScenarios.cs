using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Pipeline;

/// <summary>
/// S1 — the whole arc, for the commonest kind of outage there is: an HTTP endpoint that starts
/// answering 503.
/// <para>
/// This is the scenario the product exists for, and until now nothing had ever run it. Every piece is
/// separately tested and none of them had been watched working together: the scheduler starting a
/// runner, the checker noticing, the state machine confirming without waiting out retries because the
/// failure is hard, an incident opening, a webhook arriving, and all of it unwinding on the way back
/// up.
/// </para>
/// </summary>
public class HardDownScenarios : IClassFixture<PipelineFixture>
{
    private readonly PipelineFixture _fx;

    public HardDownScenarios(PipelineFixture fx) => _fx = fx;

    [E2EFact]
    public async Task An_HTTP_outage_travels_from_the_target_to_a_webhook_and_back()
    {
        await _fx.StartAsync();
        var app = _fx.App;
        var sink = _fx.Sink;

        var monitorId = await app.SeedMonitorAsync(
            "s1-http-toggle",
            MonitorType.Http,
            Probe.Json(new HttpMonitorConfig { Url = $"{Targets.HttpBaseUrl}/toggle" }));

        // Up first, and the deadline allows for startup jitter: MonitorRunner delays each runner by up
        // to min(interval, 15s) before its first check, so a 5-second monitor can take 10 seconds to
        // produce its first result even when everything is healthy.
        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(30));
        sink.Clear();

        using (var broken = TargetControl.Break(Target.Http))
        {
            // The break is a HARD failure — the server answered 503 — so Down is confirmed on the first
            // failing check rather than after RetryCount+1 of them. That is the behaviour that decides
            // how fast an alert arrives, and this is the only place it is observed end to end.
            var alert = await sink.WaitForAsync(monitorId, "Down", TimeSpan.FromSeconds(45));

            Assert.Equal("Down", alert.Status);
            Assert.Equal("Up", alert.PreviousStatus);
            Assert.Equal("s1-http-toggle", alert.Monitor);
            Assert.Equal("Unexpected status 503", alert.Message);

            // The incident, asserted separately from the alert and deliberately so.
            //
            // The plan predicted diagnostics.lastStatusCode == "503" on the webhook itself. That is
            // RACY and must not be asserted: AlertEnricher reads the latest PERSISTED heartbeat, while
            // MonitorRunner enqueues the heartbeat and the notification onto two independent channels
            // with no ordering between them. The enricher can therefore run before the 503 heartbeat
            // has landed and legitimately report the previous one. The 503 is asserted below, against
            // the heartbeat, where it is not a race.
            Assert.NotNull(alert.IncidentId);

            var status = await app.WaitForStatusAsync(monitorId, [MonitorStatus.Down], TimeSpan.FromSeconds(20));
            Assert.Equal(MonitorStatus.Down, status);

            var incidents = await app.IncidentsAsync(monitorId);
            var incident = Assert.Single(incidents);
            Assert.Null(incident.ResolvedAt);
            Assert.Equal(MonitorStatus.Down, incident.Severity);

            var beats = await app.HeartbeatsAsync(monitorId);
            var down = beats.Last(b => b.Status == MonitorStatus.Down);
            Assert.Equal("503", down.StatusCode);
            Assert.Equal("Unexpected status 503", down.Message);

            // Important marks the beat that CHANGED the confirmed status — the one the heartbeat bar
            // renders as a transition rather than as another red tick.
            Assert.True(down.Important);

            broken.RestoreNow();

            var recovery = await sink.WaitForAsync(monitorId, "Up", TimeSpan.FromSeconds(45));
            Assert.Equal("Up", recovery.Status);
            Assert.Equal("Down", recovery.PreviousStatus);

            await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(20));

            var resolved = Assert.Single(await app.IncidentsAsync(monitorId));
            Assert.NotNull(resolved.ResolvedAt);
            Assert.Equal(incident.Id, resolved.Id);
        }
    }

    [E2EFact]
    public async Task A_first_ever_success_does_not_alert()
    {
        // The other half of "only a real recovery notifies", and the one that would flood an operator
        // if it broke: adding twenty monitors to a healthy estate must produce zero alerts. A monitor
        // starts Pending, and going Pending → Up is not a recovery.
        await _fx.StartAsync();
        var app = _fx.App;
        var sink = _fx.Sink;

        var monitorId = await app.SeedMonitorAsync(
            "s1-quiet-start",
            MonitorType.Http,
            Probe.Json(new HttpMonitorConfig { Url = $"{Targets.HttpBaseUrl}/ok" }));

        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(30));

        // A full extra interval after it went green, so the window covers a second healthy check too.
        await sink.AssertNoneAsync(monitorId, "Up", TimeSpan.FromSeconds(8));

        Assert.Empty(await app.IncidentsAsync(monitorId));
    }
}
