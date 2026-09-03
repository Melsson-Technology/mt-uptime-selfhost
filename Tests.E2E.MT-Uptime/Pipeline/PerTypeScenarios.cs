using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Pipeline;

/// <summary>
/// S3–S6 — one monitor of each remaining type, taken down and brought back through the real engine.
/// <para>
/// Tier 1 already proved each checker's answer against each service. What this adds is that the answer
/// survives the trip: a DNS failure and a MySQL failure look nothing alike to a checker and have to
/// look identical to everything downstream. Any type whose result never reached the state machine
/// correctly would be a monitor that silently never alerts, and the only way to find that is to break
/// its target and wait.
/// </para>
/// </summary>
public class PerTypeScenarios : IClassFixture<PipelineFixture>
{
    private readonly PipelineFixture _fx;

    public PerTypeScenarios(PipelineFixture fx) => _fx = fx;

    /// <summary>
    /// Up → break → Down + alert → restore → Up + alert, for one monitor.
    /// <para>
    /// The deadlines are generous throughout and none of them is a fixed sleep. The budget for
    /// noticing an outage is startup jitter (up to one interval) plus the interval plus the timeout,
    /// and a database that has just been stopped can take a moment more; a suite that fails on a
    /// loaded box teaches people to re-run it rather than read it.
    /// </para>
    /// </summary>
    private async Task AssertOutageArcAsync(
        string name,
        MonitorType type,
        object config,
        Target target,
        int timeoutSeconds = 4,
        int intervalSeconds = 5)
    {
        await _fx.StartAsync();
        var app = _fx.App;
        var sink = _fx.Sink;

        var monitorId = await app.SeedMonitorAsync(
            name, type, Probe.Json(config), intervalSeconds: intervalSeconds, timeoutSeconds: timeoutSeconds);

        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(45));
        sink.Clear();

        using var broken = TargetControl.Break(target);

        var down = await sink.WaitForAsync(monitorId, "Down", TimeSpan.FromSeconds(90));
        Assert.Equal("Up", down.PreviousStatus);
        Assert.False(string.IsNullOrWhiteSpace(down.Message));

        var incident = Assert.Single(await app.IncidentsAsync(monitorId));
        Assert.Null(incident.ResolvedAt);

        broken.RestoreNow();

        var up = await sink.WaitForAsync(monitorId, "Up", TimeSpan.FromSeconds(90));
        Assert.Equal("Down", up.PreviousStatus);

        await app.WaitForStatusAsync(monitorId, [MonitorStatus.Up], TimeSpan.FromSeconds(30));
        Assert.NotNull(Assert.Single(await app.IncidentsAsync(monitorId)).ResolvedAt);
    }

    [E2EFact]
    public async Task S3_a_DNS_zone_going_away_opens_and_resolves_an_incident()
    {
        // Interval and timeout are both raised well above the battery's default, and that is not
        // padding — it is plan correction #8. DnsClient runs its OWN retry schedule (5 seconds per
        // attempt, twice) before giving up, which outlives a 4-second check timeout. At the default
        // cadence every silent DNS failure would surface as the runner's Down("Timeout") instead of
        // the resolver's own message, and this scenario would be testing the timeout path a second
        // time rather than the DNS path at all.
        await AssertOutageArcAsync(
            "s3-dns",
            MonitorType.Dns,
            new DnsMonitorConfig
            {
                Hostname = Targets.DnsAName,
                RecordType = "A",
                Resolver = Targets.DnsResolver,
            },
            Target.Dns,
            intervalSeconds: 20,
            timeoutSeconds: 19);
    }

    // The two database scenarios build their config AFTER StartAsync, and it has to be that way round:
    // the password must go through the running application's own ISecretProtector, and reaching for it
    // in an argument list would touch _fx.App before the fixture has booted — an expression-bodied
    // method here would throw on whichever of these xUnit happened to run first.

    [E2EFact]
    public async Task S4_a_MySQL_server_stopping_opens_and_resolves_an_incident()
    {
        await _fx.StartAsync();

        await AssertOutageArcAsync(
            "s4-mysql",
            MonitorType.MySql,
            new DbMonitorConfig
            {
                Host = Targets.MySqlHost,
                Port = Targets.MySqlPort,
                Database = Targets.MySqlDatabase,
                Username = Targets.MySqlUser,
                Password = _fx.App.Protector.Protect(Targets.MySqlPassword),
                Tls = DbTlsMode.Preferred,
            },
            Target.MySql);
    }

    [E2EFact]
    public async Task S5_a_PostgreSQL_server_stopping_opens_and_resolves_an_incident()
    {
        await _fx.StartAsync();

        await AssertOutageArcAsync(
            "s5-postgres",
            MonitorType.Postgres,
            new DbMonitorConfig
            {
                Host = Targets.PostgresHost,
                Port = Targets.PostgresPort,
                Database = Targets.PostgresDatabase,
                Username = Targets.PostgresUser,
                Password = _fx.App.Protector.Protect(Targets.PostgresPassword),
                Tls = DbTlsMode.Preferred,
            },
            Target.Postgres);
    }

    [E2EFact]
    public async Task S6_a_certificate_inside_the_warn_window_is_Down_and_its_expiry_is_persisted()
    {
        // TLS is the one type with nothing to break: the certificate is already what it is. So the
        // scenario is the other shape — two monitors, one healthy and one not, distinguished only by
        // which port they point at.
        //
        // The assertion that matters is CertExpiresAt reaching the Monitors row. That column is what
        // the dashboard renders as "expires in N days" and what AlertEnricher puts in the alert, and
        // it travels a path nothing else uses: the checker sets it on CheckResult, the runner passes
        // it to the heartbeat writer, and the writer copies it onto the monitor. Three hops that exist
        // for one field.
        await _fx.StartAsync();
        var app = _fx.App;

        var expiring = await app.SeedMonitorAsync(
            "s6-tls-expiring",
            MonitorType.Tls,
            Probe.Json(new TlsMonitorConfig
            {
                Host = Targets.Host,
                Port = Targets.HttpsExpiringPort,
                WarnDays = 14,
            }));

        var healthy = await app.SeedMonitorAsync(
            "s6-tls-valid",
            MonitorType.Tls,
            Probe.Json(new TlsMonitorConfig
            {
                Host = Targets.Host,
                Port = Targets.HttpsValidPort,
                WarnDays = 14,
            }));

        await app.WaitForStatusAsync(expiring, [MonitorStatus.Down], TimeSpan.FromSeconds(45));
        await app.WaitForStatusAsync(healthy, [MonitorStatus.Up], TimeSpan.FromSeconds(45));

        var expiringRow = await app.MonitorAsync(expiring);
        Assert.NotNull(expiringRow.CertExpiresAt);
        Assert.Equal(Targets.TlsExpiringNotAfter, expiringRow.CertExpiresAt!.Value, TimeSpan.FromSeconds(1));

        // The healthy one carries it too. A monitor that only recorded an expiry when it was ALREADY
        // complaining would be useless for the thing operators actually want — a dashboard that says
        // how long every certificate has left, before any of them is urgent.
        var healthyRow = await app.MonitorAsync(healthy);
        Assert.NotNull(healthyRow.CertExpiresAt);
        Assert.Equal(Targets.TlsValidNotAfter, healthyRow.CertExpiresAt!.Value, TimeSpan.FromSeconds(1));
    }

    [E2EFact]
    public async Task S10_an_upside_down_monitor_is_Up_when_its_target_refuses()
    {
        // For an endpoint that must NOT answer — a port that should be firewalled, a debug listener
        // that should not be running in production. The inversion happens in the state machine, ahead
        // of everything else, so a refused connection becomes a healthy check and travels the ordinary
        // route from there.
        await _fx.StartAsync();
        var app = _fx.App;

        var inverted = await app.SeedMonitorAsync(
            "s10-upside-down",
            MonitorType.Tcp,
            Probe.Json(new TcpMonitorConfig { Host = Targets.Host, Port = Targets.TcpRefusedPort }),
            upsideDown: true);

        // The control: the same closed port, the right way up, must be Down. Without it this test
        // would pass on a monitor that reported Up for everything.
        var normal = await app.SeedMonitorAsync(
            "s10-right-way-up",
            MonitorType.Tcp,
            Probe.Json(new TcpMonitorConfig { Host = Targets.Host, Port = Targets.TcpRefusedPort }));

        Assert.Equal(MonitorStatus.Up, await app.WaitForStatusAsync(inverted, [MonitorStatus.Up], TimeSpan.FromSeconds(45)));
        Assert.Equal(MonitorStatus.Down, await app.WaitForStatusAsync(normal, [MonitorStatus.Down], TimeSpan.FromSeconds(45)));
    }
}
