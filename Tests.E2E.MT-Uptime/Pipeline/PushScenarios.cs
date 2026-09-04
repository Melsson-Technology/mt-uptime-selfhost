using System.Net;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Pipeline;

/// <summary>
/// S9 — the dead-man's switch. The only monitor type nothing reaches out for.
/// <para>
/// Push runs on a path of its own: no checker, no <c>MonitorRunner</c>, no interval loop. A ping
/// arrives at an anonymous endpoint, <c>PushMonitorManager</c> records it, and a watchdog on a fixed
/// fifteen-second tick marks anything overdue as Down. It ends up in the same heartbeats, incidents
/// and notifications as every other type — by way of a deliberate mirror of <c>MonitorRunner.Process</c>
/// — and duplicated code that has to stay in step is exactly what a scenario test is for.
/// </para>
/// </summary>
public class PushScenarios : IClassFixture<PipelineFixture>
{
    private readonly PipelineFixture _fx;

    public PushScenarios(PipelineFixture fx) => _fx = fx;

    [E2EFact]
    public async Task A_ping_reports_Up_and_its_absence_reports_Down()
    {
        await _fx.StartAsync();
        var app = _fx.App;
        var sink = _fx.Sink;
        var client = await app.EnsureStartedAsync();

        // Interval 5 + grace 5 gives a 10-second window. Detection then costs that window plus up to
        // one 15-second watchdog tick, on top of the watchdog's own 20-second startup delay — so the
        // deadline below has to clear about 45 seconds. That arithmetic is why this scenario is slow
        // and cannot be tuned faster: MinIntervalSeconds is 5 and the tick is a constant.
        var token = PushMonitorManager.NewToken();
        var monitorId = await app.SeedMonitorAsync(
            "s9-push",
            MonitorType.Push,
            Probe.Json(new PushMonitorConfig { Token = token, GraceSeconds = 5 }),
            intervalSeconds: 5);

        // A ping through the application's own endpoint, not through the manager: the route is
        // anonymous, rate-limited and mapped separately, and going around it would skip all three.
        var ping = await client.GetAsync($"/ping/{token}");
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);

        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(20));
        sink.Clear();

        // Now stop pinging. Nothing to break here — the outage IS the silence.
        var alert = await sink.WaitForAsync(monitorId, "Down", TimeSpan.FromSeconds(75));
        Assert.Equal("Up", alert.PreviousStatus);
        Assert.Contains("No ping received", alert.Message, StringComparison.Ordinal);

        var incident = Assert.Single(await app.IncidentsAsync(monitorId));
        Assert.Null(incident.ResolvedAt);

        // And back. A ping recovers immediately — there is no interval to wait for, because the ping
        // itself is the check.
        var recoveryPing = await client.GetAsync($"/ping/{token}");
        Assert.Equal(HttpStatusCode.OK, recoveryPing.StatusCode);

        var recovery = await sink.WaitForAsync(monitorId, "Up", TimeSpan.FromSeconds(30));
        Assert.Equal("Down", recovery.PreviousStatus);
        Assert.Equal("Ping received", recovery.Message);

        await app.WaitForIncidentResolvedAsync(monitorId);
    }

    [E2ETheory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("HEAD")]
    public async Task Every_documented_method_is_accepted(string method)
    {
        // The endpoint takes all three so that any cron, curl, wget or PowerShell one-liner works
        // without the operator having to think about it — which is the entire ergonomic argument for
        // push monitors. A method quietly falling to 405 would be a monitor that reports Down while
        // the job it watches is running perfectly.
        await _fx.StartAsync();
        var app = _fx.App;
        var client = await app.EnsureStartedAsync();

        var token = PushMonitorManager.NewToken();
        var monitorId = await app.SeedMonitorAsync(
            $"s9-method-{method}",
            MonitorType.Push,
            Probe.Json(new PushMonitorConfig { Token = token, GraceSeconds = 30 }),
            intervalSeconds: 60);

        using var request = new HttpRequestMessage(new HttpMethod(method), $"/ping/{token}");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(20));
    }

    [E2EFact]
    public async Task An_unknown_token_is_404_and_moves_nothing()
    {
        // The token is the only credential this endpoint has. A 404 is the right answer, and the
        // second assertion is the one that matters: a ping for a token nobody owns must not touch a
        // real monitor's state.
        await _fx.StartAsync();
        var app = _fx.App;
        var client = await app.EnsureStartedAsync();

        var token = PushMonitorManager.NewToken();
        var monitorId = await app.SeedMonitorAsync(
            "s9-untouched",
            MonitorType.Push,
            Probe.Json(new PushMonitorConfig { Token = token, GraceSeconds = 30 }),
            intervalSeconds: 60);

        await client.GetAsync($"/ping/{token}");
        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(20));
        var beforeBeats = (await app.HeartbeatsAsync(monitorId)).Count;

        var response = await client.GetAsync($"/ping/{PushMonitorManager.NewToken()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Unknown ping token", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(beforeBeats, (await app.HeartbeatsAsync(monitorId)).Count);
    }

    [E2EFact]
    public async Task A_push_monitor_with_no_token_can_never_be_pinged()
    {
        // Documents a configuration that produces a monitor which cannot work. PushMonitorManager
        // refuses to register it and says so at Warning — but the row still exists, still appears on
        // the dashboard, and will sit at Pending forever without ever alerting.
        //
        // Worth pinning because "it is on the dashboard and it is not red" is exactly how a monitor
        // that does nothing goes unnoticed.
        await _fx.StartAsync();
        var app = _fx.App;

        var monitorId = await app.SeedMonitorAsync(
            "s9-tokenless",
            MonitorType.Push,
            Probe.Json(new PushMonitorConfig { Token = "", GraceSeconds = 5 }),
            intervalSeconds: 5);

        // Longer than the watchdog's startup grace plus a tick, so a monitor that WAS registered would
        // have been marked overdue by now.
        await Task.Delay(TimeSpan.FromSeconds(40));

        var row = await app.MonitorAsync(monitorId);
        Assert.Equal(MonitorStatus.Pending, row.CurrentStatus);
        Assert.Empty(await app.HeartbeatsAsync(monitorId));
    }
}
