using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Pipeline;

/// <summary>
/// S7 — the slow-but-answering state, which is the one an operator most wants to hear about early and
/// the one a naive monitor cannot see at all.
/// <para>
/// Degraded is decided entirely by the runner and the state machine — no checker ever returns it. A
/// successful check whose response time exceeds <c>SlowThresholdMs</c> starts a streak, the beats are
/// recorded as Degraded so the history shows the slowdown, and only <c>DegradedAfterChecks</c>
/// consecutive slow checks confirm it and alert. One fast check clears the streak outright.
/// </para>
/// <para>
/// This is why <c>fixture-server.py</c> sleeps <b>before</b> writing its response line. The checker
/// measures to the headers, so a fixture that slept after them would report every probe as fast and
/// every test in this file would pass while proving nothing.
/// </para>
/// </summary>
public class DegradedScenarios : IClassFixture<PipelineFixture>
{
    private readonly PipelineFixture _fx;

    public DegradedScenarios(PipelineFixture fx) => _fx = fx;

    [E2EFact]
    public async Task A_sustained_slowdown_confirms_Degraded_and_alerts_once()
    {
        await _fx.StartAsync();
        var app = _fx.App;
        var sink = _fx.Sink;

        // The cadence has to hold two slow checks inside one interval each. `break http-slow` makes
        // /toggle sleep, so the interval and timeout are raised to leave room for a response that is
        // deliberately late without turning it into a timeout — a slow check that times out is a Down,
        // not a Degraded, and the two must not be confused.
        var monitorId = await app.SeedMonitorAsync(
            "s7-degraded",
            MonitorType.Http,
            Probe.Json(new HttpMonitorConfig { Url = $"{Targets.HttpBaseUrl}/toggle" }),
            intervalSeconds: 10,
            timeoutSeconds: 9,
            slowThresholdMs: 500,
            degradedAfterChecks: 2);

        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(40));
        var healthyBeats = (await app.HeartbeatsAsync(monitorId)).Count;
        sink.Clear();

        using (var broken = TargetControl.Break(Target.HttpSlow))
        {
            var alert = await sink.WaitForAsync(monitorId, "Degraded", TimeSpan.FromSeconds(90));

            Assert.Equal("Degraded", alert.Status);
            Assert.Equal("Up", alert.PreviousStatus);

            // Slow, not failed: the response time on the alert has to be over the threshold, which is
            // the whole basis for the decision. A Degraded alert carrying a fast time would mean the
            // streak was being counted on something other than what it claims.
            Assert.NotNull(alert.ResponseTimeMs);
            Assert.True(alert.ResponseTimeMs > 500,
                $"a Degraded alert reported {alert.ResponseTimeMs:0}ms, which is under the 500ms threshold");

            var beats = (await app.HeartbeatsAsync(monitorId)).Skip(healthyBeats).ToList();

            // The FIRST slow beat is already recorded as Degraded even though nothing was sent for it
            // — the same shape as Pending during a retry window. The history shows the slowdown from
            // the moment it starts; the alert waits for the streak.
            Assert.Equal(MonitorStatus.Degraded, beats[0].Status);
            Assert.False(beats[0].Important);
            Assert.Equal(MonitorStatus.Degraded, beats[1].Status);
            Assert.True(beats[1].Important);

            // Exactly one alert, however long it stays slow. Already-degraded beats keep being
            // recorded and must not re-notify.
            Assert.Equal(1, sink.Received.Count(a => a.MonitorId == monitorId && a.Kind == "Degraded"));

            var incident = Assert.Single(await app.IncidentsAsync(monitorId));
            Assert.Equal(MonitorStatus.Degraded, incident.Severity);

            broken.RestoreNow();

            var recovery = await sink.WaitForAsync(monitorId, "Up", TimeSpan.FromSeconds(60));
            Assert.Equal("Degraded", recovery.PreviousStatus);

            Assert.NotNull(Assert.Single(await app.IncidentsAsync(monitorId)).ResolvedAt);
        }
    }

    [E2EFact]
    public async Task One_fast_check_clears_a_building_slow_streak()
    {
        // The property that stops a single slow response from eventually adding up to an alert across
        // an afternoon. The streak must be CONSECUTIVE, and a healthy check resets it to zero rather
        // than decrementing it.
        //
        // DegradedAfterChecks is 3 here so there is room to interrupt at 1 and still have the test
        // mean something: break slow, see one Degraded beat, restore, and confirm the confirmation
        // never comes.
        await _fx.StartAsync();
        var app = _fx.App;
        var sink = _fx.Sink;

        var monitorId = await app.SeedMonitorAsync(
            "s7-streak-reset",
            MonitorType.Http,
            Probe.Json(new HttpMonitorConfig { Url = $"{Targets.HttpBaseUrl}/toggle" }),
            intervalSeconds: 10,
            timeoutSeconds: 9,
            slowThresholdMs: 500,
            degradedAfterChecks: 3);

        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(40));
        var before = (await app.HeartbeatsAsync(monitorId)).Count;
        sink.Clear();

        using (var broken = TargetControl.Break(Target.HttpSlow))
        {
            // One slow beat, then straight back to fast. Waiting for the beat rather than sleeping:
            // restoring before a single slow check had landed would make this pass without ever
            // building a streak to reset.
            var beats = await app.WaitForHeartbeatsAsync(monitorId, before + 1, TimeSpan.FromSeconds(40));
            Assert.Equal(MonitorStatus.Degraded, beats[before].Status);
            Assert.False(beats[before].Important);

            broken.RestoreNow();
        }

        // Three more intervals — comfortably more than the three consecutive slow checks that would
        // have been needed — with the target healthy throughout. If the streak had merely been paused
        // rather than cleared, this window is long enough for it to finish.
        await sink.AssertNoneAsync(monitorId, "Degraded", TimeSpan.FromSeconds(35));

        Assert.Equal(MonitorStatus.Up, (await app.MonitorAsync(monitorId)).CurrentStatus);
        Assert.Empty(await app.IncidentsAsync(monitorId));
    }
}
