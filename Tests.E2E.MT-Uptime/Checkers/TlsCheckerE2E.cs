using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Checkers;

/// <summary>
/// <see cref="TlsChecker"/> against four ports carrying four deliberately different certificates:
/// valid for a year, expiring in five days, already expired, and issued by a CA this box does not
/// trust.
/// <para>
/// Also with no behavioural tests before this file — and the hardest of the six to reason about
/// without one, because its answer is arithmetic against the clock. A certificate minted with
/// <c>-enddate</c> five days out is "5d" for a few hours and "4d" for the rest, so every assertion
/// here has to be written as a boundary rather than a value.
/// </para>
/// </summary>
public class TlsCheckerE2E : IClassFixture<CheckerHost>
{
    private readonly IMonitorChecker _tls;

    public TlsCheckerE2E(CheckerHost host) => _tls = host.For(MonitorType.Tls);

    private Task<CheckResult> ProbeAsync(int port, int warnDays = 14, string? host = null) =>
        Probe.RunAsync(_tls, Probe.Context(MonitorType.Tls,
            new TlsMonitorConfig { Host = host ?? Targets.Host, Port = port, WarnDays = warnDays }));

    [E2EFact]
    public async Task A_certificate_with_a_year_left_is_Up()
    {
        var result = await ProbeAsync(Targets.HttpsValidPort);

        Assert.Equal(CheckStatus.Up, result.Status);

        // CertExpiresAt against the manifest, not against "roughly a year": install-targets.sh records
        // each leaf's exact notAfter when it mints it, so this asserts the checker read the same
        // certificate the installer created rather than something else answering on that port.
        Assert.NotNull(result.CertExpiresAt);
        Assert.Equal(Targets.TlsValidNotAfter, result.CertExpiresAt!.Value, TimeSpan.FromSeconds(1));

        // Both fields carry the day count, in two different renderings, and the plan predicted only
        // the Message. StatusCode is "364d"; Message is "Valid for 364d".
        Assert.Matches(@"^\d+d$", result.StatusCode!);
        Assert.StartsWith("Valid for ", result.Message, StringComparison.Ordinal);
        Assert.False(result.Hard);
    }

    [E2EFact]
    public async Task A_certificate_inside_the_warn_window_is_Down()
    {
        // The certificate expires in five days and the default warn window is fourteen, so this is the
        // ordinary "renew it" alert — the reason the monitor type exists.
        var result = await ProbeAsync(Targets.HttpsExpiringPort, warnDays: 14);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.StartsWith("Certificate expires in ", result.Message, StringComparison.Ordinal);
        Assert.Equal(Targets.TlsExpiringNotAfter, result.CertExpiresAt!.Value, TimeSpan.FromSeconds(1));

        // Soft: a certificate is not going to renew itself inside the retry window, but the failure is
        // still not a definitive negative answer from the server the way a bad HTTP status is. Pinned
        // because "obviously this should be hard" is a plausible-sounding change that would alter when
        // every certificate alert fires.
        Assert.False(result.Hard);
    }

    [E2EFact]
    public async Task The_warn_window_is_what_decides_it()
    {
        // Same port, same certificate, same moment — only the threshold differs. This is the test that
        // proves the previous one is measuring WarnDays and not merely "that port is unhealthy".
        var strict = await ProbeAsync(Targets.HttpsExpiringPort, warnDays: 14);
        var relaxed = await ProbeAsync(Targets.HttpsExpiringPort, warnDays: 2);

        Assert.Equal(CheckStatus.Down, strict.Status);
        Assert.Equal(CheckStatus.Up, relaxed.Status);
    }

    [E2EFact]
    public async Task The_comparison_is_inclusive_at_the_boundary()
    {
        // daysLeft <= warnDays, not <. A monitor set to warn at 14 days must fire ON day 14, not the
        // day after — an off-by-one here is a whole day of silence at exactly the moment the alert
        // was configured to arrive.
        //
        // Derived from the certificate rather than assumed: whatever the checker reports as the day
        // count, setting WarnDays to that exact number must be Down and one lower must be Up.
        var probe = await ProbeAsync(Targets.HttpsExpiringPort, warnDays: 1);

        // The "expiring" certificate is minted five days out, and this test needs at least two days
        // left to have a lower threshold to compare against. A scratch box kept for a week would
        // otherwise fail here with a FormatException on "expired" or an off-by-one against the
        // Math.Max(1, WarnDays) floor — a confusing way to be told the box has gone stale.
        Assert.True(
            int.TryParse(probe.StatusCode?.TrimEnd('d'), out var daysLeft) && daysLeft >= 2,
            $"the expiring certificate now reports '{probe.StatusCode}', so this boundary can no longer "
            + "be measured. It is minted five days out: re-run 'sudo ./e2e/install-targets.sh --only certs' "
            + "to refresh it.");

        var atTheBoundary = await ProbeAsync(Targets.HttpsExpiringPort, warnDays: daysLeft);
        var justInside = await ProbeAsync(Targets.HttpsExpiringPort, warnDays: daysLeft - 1);

        Assert.Equal(CheckStatus.Down, atTheBoundary.Status);
        Assert.Equal(CheckStatus.Up, justInside.Status);
    }

    [E2EFact]
    public async Task An_expired_certificate_is_Down_and_says_how_long_ago()
    {
        var result = await ProbeAsync(Targets.HttpsExpiredPort);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Contains("expired", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ago", result.Message, StringComparison.Ordinal);

        // A distinct StatusCode, so a dashboard can tell "expired" from "expiring in 3d" without
        // parsing prose.
        Assert.Equal("expired", result.StatusCode);
        Assert.Equal(Targets.TlsExpiredNotAfter, result.CertExpiresAt!.Value, TimeSpan.FromSeconds(1));
    }

    [E2EFact]
    public async Task An_untrusted_issuer_is_Up_because_the_chain_is_deliberately_not_checked()
    {
        // DOCUMENTS A REAL LIMIT, and it is the reason this test is worded as it is rather than as a
        // bug report. TlsChecker installs a RemoteCertificateValidationCallback that returns true for
        // everything, on purpose: it has to be able to READ an expired or self-signed certificate to
        // report on it, and a validating handshake would fail before the certificate was available.
        //
        // The consequence is that a TLS monitor answers only one question — when does this certificate
        // expire — and says nothing about whether a browser would accept it. A certificate from a CA
        // nobody trusts, with a year left, is Up.
        //
        // If that ever changes, this test fails, and that is correct: it would be a behaviour change
        // every existing TLS monitor's operator should be told about.
        var result = await ProbeAsync(Targets.HttpsUntrustedPort);

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.NotNull(result.CertExpiresAt);
    }

    [E2ETheory]
    [InlineData("MYSQL_PORT")]
    [InlineData("POSTGRES_PORT")]
    [InlineData("HTTP_PORT")]
    public async Task A_port_that_does_not_start_with_TLS_is_Down(string manifestKey)
    {
        // The other half of the scope: this monitor speaks TLS immediately after connecting. MySQL and
        // PostgreSQL both send a plaintext greeting first and negotiate TLS afterwards, and 8081 is
        // plain HTTP — so all three connect fine and fail the handshake.
        //
        // Worth pinning because "point a TLS monitor at your database" is an obvious thing for an
        // operator to try, and the answer is a permanent Down rather than a certificate report.
        var result = await ProbeAsync(Targets.Int(manifestKey));

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Null(result.CertExpiresAt);
    }

    [E2EFact]
    public async Task A_closed_port_is_Down()
    {
        var result = await ProbeAsync(Targets.TcpRefusedPort);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Contains("refused", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task A_blackholed_port_hangs_until_the_probe_is_cancelled()
    {
        var ctx = Probe.Context(MonitorType.Tls,
            new TlsMonitorConfig { Host = Targets.Host, Port = Targets.TcpBlackholePort });

        await Probe.AssertCancelledAsync(_tls, ctx, TimeSpan.FromSeconds(3));
    }

    [E2ETheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_unconfigured_monitor_says_so_instead_of_probing(string host)
    {
        var result = await ProbeAsync(Targets.HttpsValidPort, host: host);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Equal("Host not configured", result.Message);
    }

    [E2EFact]
    public async Task Port_zero_falls_back_to_443_rather_than_failing()
    {
        // cfg.Port > 0 ? cfg.Port : 443. Nothing listens on 443 on this box, so the observable proof
        // is that the probe reaches the network and is refused — not that it reports a missing port.
        // Documents the fallback; a monitor created without a port is a 443 monitor.
        var result = await ProbeAsync(port: 0);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Contains("refused", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
