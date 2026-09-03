using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Pipeline;

/// <summary>
/// S2 and S8 — the retry window, and the timeout that fills it.
/// <para>
/// The retry cushion is the difference between a monitoring tool people keep and one whose alerts get
/// muted. A soft failure has to be seen <c>RetryCount + 1</c> times consecutively before anyone is
/// told, the beats in between have to be recorded so the history shows what happened, and exactly one
/// alert has to come out the far end. Every part of that is easy to state and none of it had been
/// watched happening.
/// </para>
/// </summary>
public class SoftDownRetriesScenarios : IClassFixture<PipelineFixture>
{
    private readonly PipelineFixture _fx;

    public SoftDownRetriesScenarios(PipelineFixture fx) => _fx = fx;

    [E2EFact]
    public async Task A_soft_failure_records_Pending_beats_and_alerts_once()
    {
        await _fx.StartAsync();
        var app = _fx.App;
        var sink = _fx.Sink;

        // RetryCount 2 means three consecutive failures before Down is confirmed: the state machine
        // increments first and then tests failCount > retryCount, so 1 and 2 are Pending and 3 confirms.
        var monitorId = await app.SeedMonitorAsync(
            "s2-tcp-retries",
            MonitorType.Tcp,
            Probe.Json(new TcpMonitorConfig { Host = Targets.Host, Port = Targets.TcpPort }),
            retryCount: 2);

        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(30));

        var healthyBeats = (await app.HeartbeatsAsync(monitorId)).Count;
        sink.Clear();

        using (var broken = TargetControl.Break(Target.Tcp))
        {
            // Three more beats: Pending, Pending, Down. Waiting on the count rather than on the status,
            // because CurrentStatus and the heartbeat rows are written down two independent paths — a
            // status of Down does not guarantee the beats behind it have landed yet.
            var beats = await app.WaitForHeartbeatsAsync(monitorId, healthyBeats + 3, TimeSpan.FromSeconds(60));
            var afterBreak = beats.Skip(healthyBeats).Take(3).ToList();

            Assert.Equal(
                [MonitorStatus.Pending, MonitorStatus.Pending, MonitorStatus.Down],
                afterBreak.Select(b => b.Status));

            // Attempt counts up across the window, which is what the UI shows as "attempt 2 of 3".
            Assert.Equal([1, 2, 3], afterBreak.Select(b => b.Attempt));

            // Only the confirming beat is important. If the Pending ones were, the heartbeat bar would
            // draw three transitions for one outage.
            Assert.Equal([false, false, true], afterBreak.Select(b => b.Important));

            var alert = await sink.WaitForAsync(monitorId, "Down", TimeSpan.FromSeconds(30));
            Assert.Equal("Up", alert.PreviousStatus);

            // Exactly one. The two Pending beats must produce nothing: an alert per failed attempt is
            // how a flapping target becomes an ignored channel.
            var downAlerts = sink.Received.Count(a => a.MonitorId == monitorId && a.Kind == "Down");
            Assert.Equal(1, downAlerts);

            broken.RestoreNow();
            await sink.WaitForAsync(monitorId, "Up", TimeSpan.FromSeconds(45));
        }
    }

    [E2EFact]
    public async Task A_recovery_inside_the_retry_window_never_alerts_at_all()
    {
        // The point of the cushion, stated as a test. A blip that resolves before the window closes
        // must be invisible to every notification channel — recorded in the history, never paged.
        //
        // Timing is the risk here rather than the behaviour: the break and restore have to fit inside
        // RetryCount+1 checks. RetryCount 5 at a 5-second interval gives a 30-second window, and the
        // helper's break and restore are each a second or two.
        await _fx.StartAsync();
        var app = _fx.App;
        var sink = _fx.Sink;

        var monitorId = await app.SeedMonitorAsync(
            "s2-blip",
            MonitorType.Tcp,
            Probe.Json(new TcpMonitorConfig { Host = Targets.Host, Port = Targets.TcpPort }),
            retryCount: 5);

        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(30));
        var before = (await app.HeartbeatsAsync(monitorId)).Count;
        sink.Clear();

        using (var broken = TargetControl.Break(Target.Tcp))
        {
            // One failing beat is enough to prove the outage was seen at all; without waiting for it,
            // a fast restore could make this test pass by never breaking anything.
            await app.WaitForHeartbeatsAsync(monitorId, before + 1, TimeSpan.FromSeconds(30));
            var pending = (await app.HeartbeatsAsync(monitorId)).Skip(before).First();
            Assert.Equal(MonitorStatus.Pending, pending.Status);

            broken.RestoreNow();
        }

        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(30));

        // Neither direction: no Down, because it was never confirmed — and therefore no Up either,
        // because there was no outage to recover from.
        await sink.AssertNoneAsync(monitorId, "Down", TimeSpan.FromSeconds(2));
        await sink.AssertNoneAsync(monitorId, "Up", TimeSpan.FromSeconds(2));
        Assert.Empty(await app.IncidentsAsync(monitorId));
    }

    [E2EFact]
    public async Task A_check_that_never_answers_becomes_a_timeout()
    {
        // S8. The blackholed port swallows the SYN, so the probe cannot finish — and this is the one
        // place the message comes from the RUNNER rather than from a checker.
        //
        // Tier 1 pins the other half: every checker rethrows OperationCanceledException instead of
        // returning a result. MonitorRunner catches it, distinguishes a per-check timeout from
        // application shutdown, and synthesises Down("Timeout"). Neither half means anything without
        // the other.
        await _fx.StartAsync();
        var app = _fx.App;

        var monitorId = await app.SeedMonitorAsync(
            "s8-blackhole",
            MonitorType.Tcp,
            Probe.Json(new TcpMonitorConfig { Host = Targets.Host, Port = Targets.TcpBlackholePort }),
            intervalSeconds: 5,
            timeoutSeconds: 2);

        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Down], TimeSpan.FromSeconds(45));

        var beats = await app.HeartbeatsAsync(monitorId);
        var down = beats.Last(b => b.Status == MonitorStatus.Down);

        Assert.Equal("Timeout", down.Message);

        // Soft, not hard: nothing answered, so nothing made a definitive statement. With RetryCount 0
        // it still confirms on the first failure — the cushion is zero — but the flag matters, because
        // an operator who raises RetryCount expects a timeout to respect it.
        Assert.True(down.Important);

        // The response time is the timeout itself, which is how the runner records a check it had to
        // abandon rather than one it measured.
        Assert.NotNull(down.ResponseTimeMs);
        Assert.True(down.ResponseTimeMs >= 1500,
            $"expected roughly the 2s timeout, got {down.ResponseTimeMs:0}ms");
    }
}
