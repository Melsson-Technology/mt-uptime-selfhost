using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Checkers;

/// <summary>
/// <see cref="TcpChecker"/> against real sockets.
/// <para>
/// It had <b>no behavioural tests at all</b> before this file. The whole checker is one
/// <c>ConnectAsync</c>, which is exactly the kind of code that looks too simple to test and then turns
/// out to differ from expectation in every detail that matters: which field carries the address, which
/// failures are hard, and what a blackholed port does to a probe that was never given a deadline.
/// </para>
/// </summary>
public class TcpCheckerE2E : IClassFixture<CheckerHost>
{
    private readonly IMonitorChecker _tcp;

    public TcpCheckerE2E(CheckerHost host) => _tcp = host.For(MonitorType.Tcp);

    private Task<CheckResult> ProbeAsync(string host, int port, TimeSpan? cancelAfter = null) =>
        Probe.RunAsync(_tcp, Probe.Context(MonitorType.Tcp, new TcpMonitorConfig { Host = host, Port = port }), cancelAfter);

    [E2ETheory]
    [InlineData("TCP_PORT")]
    [InlineData("MYSQL_PORT")]
    [InlineData("POSTGRES_PORT")]
    [InlineData("HTTP_PORT")]
    public async Task A_listening_port_is_Up(string manifestKey)
    {
        // Four different servers, one assertion: the checker cares only that something accepted the
        // connection. That is the whole contract of a TCP monitor and it is worth pinning across
        // several real listeners rather than one, because "the port answers" is easy to get right for
        // a fixture written to be probed and less easy against mysqld.
        var port = Targets.Int(manifestKey);

        var result = await ProbeAsync(Targets.Host, port);

        Assert.Equal(CheckStatus.Up, result.Status);

        // CORRECTED PREDICTION. The plan expected "127.0.0.1:8082" in Message. It is in StatusCode:
        // CheckResult.Up(ms, statusCode) leaves Message null. Worth pinning precisely, because an
        // assertion written against the wrong field would have passed on null == null had it used
        // Contains, and this is the field the dashboard renders.
        Assert.Equal($"{Targets.Host}:{port}", result.StatusCode);
        Assert.Null(result.Message);
        Assert.NotNull(result.ResponseTimeMs);
        Assert.False(result.Hard);
    }

    [E2EFact]
    public async Task A_closed_port_is_a_soft_Down()
    {
        // Soft, not hard. A refused connection is transient by nature — a service restarting refuses
        // for a second — so it has to burn the retry cushion rather than confirm Down immediately.
        // Only HTTP produces a hard Down, and only for a status code the server actually chose to send.
        var result = await ProbeAsync(Targets.Host, Targets.TcpRefusedPort);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);

        // The OS message, not ours: TcpChecker passes ex.Message straight through. Matching loosely on
        // "refused" because the exact wording is the platform's ("Connection refused" on Linux).
        Assert.Contains("refused", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task A_blackholed_port_hangs_until_the_probe_is_cancelled()
    {
        // The nftables DROP target, and the reason it exists rather than an unroutable address: a
        // dropped SYN produces no answer at all, so connect() waits for the OS retry schedule —
        // minutes, not seconds.
        //
        // The checker does NOT return Down("Timeout") here. It rethrows the cancellation, and the
        // runner turns that into the timeout message one layer up. See Probe.AssertCancelledAsync.
        var ctx = Probe.Context(MonitorType.Tcp,
            new TcpMonitorConfig { Host = Targets.Host, Port = Targets.TcpBlackholePort });

        await Probe.AssertCancelledAsync(_tcp, ctx, TimeSpan.FromSeconds(3));
    }

    [E2EFact]
    public async Task An_unresolvable_host_is_a_soft_Down()
    {
        // .invalid is reserved by RFC 2606 precisely so it can never resolve, which makes this the one
        // name-resolution failure that cannot start working because somebody registered a domain.
        var result = await ProbeAsync("no-such-host.invalid", Targets.TcpPort, TimeSpan.FromSeconds(15));

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [E2ETheory]
    [InlineData("", 8082)]
    [InlineData("   ", 8082)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", -1)]
    [InlineData("127.0.0.1", 70000)]
    public async Task An_unconfigured_monitor_says_so_instead_of_probing(string host, int port)
    {
        // The guard runs before the socket, so this costs no network at all — and it must, because a
        // half-configured monitor that tried to connect to port 0 would report a confusing OS error
        // and look like the target's fault.
        var result = await ProbeAsync(host, port);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Equal("Host/port not configured", result.Message);
        Assert.Null(result.ResponseTimeMs);
    }

    [E2EFact]
    public async Task Break_and_restore_moves_the_listener_Down_and_back()
    {
        // The first test in the battery to actually take something away. Everything above probes a
        // static box; this proves the break/restore helper and the checker agree about what "down"
        // means, which is the foundation the whole pipeline tier stands on.
        var before = await ProbeAsync(Targets.Host, Targets.TcpPort);
        Assert.Equal(CheckStatus.Up, before.Status);

        using (var broken = TargetControl.Break(Target.Tcp))
        {
            // No polling: the helper already waited for the port to actually refuse before returning.
            var during = await ProbeAsync(Targets.Host, Targets.TcpPort);
            Assert.Equal(CheckStatus.Down, during.Status);
            Assert.Contains("refused", during.Message, StringComparison.OrdinalIgnoreCase);

            broken.RestoreNow();

            var after = await ProbeAsync(Targets.Host, Targets.TcpPort);
            Assert.Equal(CheckStatus.Up, after.Status);
        }
    }
}
