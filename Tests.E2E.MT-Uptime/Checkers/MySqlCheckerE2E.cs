using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Core.Security;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Checkers;

/// <summary>
/// <see cref="MySqlChecker"/> against a real MySQL 8, serving TLS from the CA
/// <c>install-targets.sh</c> minted and installed into this box's trust store.
/// <para>
/// The four <see cref="DbTlsMode"/> values are the point of this file. They are the one place in the
/// product where a monitor's configuration decides how much protection a credential gets in transit,
/// the difference between them is invisible from a unit test, and the weakest of them is the default
/// — a decision the product documents at length and deliberately kept. Proving all four behave as
/// their documentation says is the only way that decision stays honest.
/// </para>
/// </summary>
public class MySqlCheckerE2E : IClassFixture<CheckerHost>
{
    private readonly IMonitorChecker _mysql;
    private readonly ISecretProtector _protector;

    public MySqlCheckerE2E(CheckerHost host)
    {
        _mysql = host.For(MonitorType.MySql);
        _protector = host.Protector;
    }

    private Task<CheckResult> ProbeAsync(
        string? host = null,
        int? port = null,
        string? database = null,
        string? username = null,
        string? password = null,
        DbTlsMode tls = DbTlsMode.Preferred,
        TimeSpan? timeout = null)
        => Probe.RunAsync(_mysql, Probe.Context(MonitorType.MySql, new DbMonitorConfig
        {
            Host = host ?? Targets.MySqlHost,
            Port = port ?? Targets.MySqlPort,
            Database = database ?? Targets.MySqlDatabase,
            Username = username ?? Targets.MySqlUser,
            // Through Protect, not as a literal. The protector is a passthrough here, so the value is
            // unchanged — but the checker's Reveal path runs either way, which is what makes this a
            // test of the credential pipeline rather than of a string.
            Password = _protector.Protect(password ?? Targets.MySqlPassword),
            Tls = tls,
        }, timeout ?? TimeSpan.FromSeconds(15)));

    [E2ETheory]
    [InlineData(DbTlsMode.Preferred)]
    [InlineData(DbTlsMode.Required)]
    [InlineData(DbTlsMode.VerifyCa)]
    public async Task Every_TLS_mode_up_to_VerifyCa_connects(DbTlsMode tls)
    {
        // VerifyCa is the strongest mode reachable with 127.0.0.1 as the host, and it is a real
        // assertion rather than a formality: it fails unless the server's certificate chains to a CA
        // in this box's trust store, which is only true because install-targets.sh put ours there with
        // update-ca-certificates. If that step regressed, this row goes red and the two weaker modes
        // stay green — which is precisely the signal wanted.
        var result = await ProbeAsync(tls: tls);

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.NotNull(result.ResponseTimeMs);
        Assert.Null(result.StatusCode);   // the DB checkers report neither a code nor a message on success
        Assert.Null(result.Message);
    }

    [E2EFact]
    public async Task VerifyFull_connects_when_the_host_matches_the_certificate()
    {
        // VerifyFull additionally checks the name. The leaf carries localhost, 127.0.0.1 and ::1 in its
        // SAN, so both spellings work — and "localhost" is used here rather than the manifest's
        // 127.0.0.1 because an IP in a SAN is a different X.509 name type from a DNS name, and a
        // library that handled only one of them would otherwise pass this test by accident.
        var result = await ProbeAsync(host: "localhost", tls: DbTlsMode.VerifyFull);

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task A_wrong_password_is_a_soft_Down_naming_the_denial()
    {
        // Soft. An authentication failure looks definitive, but a database that is mid-restart or has
        // hit max_connections produces errors in this same family, and confirming Down without the
        // retry cushion would page on both.
        var result = await ProbeAsync(password: "definitely-not-the-password");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
        Assert.Contains("Access denied", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task A_blank_password_against_a_passworded_account_is_Down()
    {
        // Worth its own row because the server's message differs — it ends "(using password: NO)" —
        // and that string is the operator's clue that the monitor was saved without a password at all,
        // rather than with a wrong one.
        var result = await ProbeAsync(password: "");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Contains("Access denied", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task An_unknown_database_is_Down()
    {
        var result = await ProbeAsync(database: "no_such_database_e2e");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Contains("Unknown database", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task A_blank_database_still_connects()
    {
        // MySqlConnectionStringBuilder accepts an empty Database and connects without selecting one;
        // SELECT 1 needs no schema. So a MySQL monitor with the database field left empty is a valid
        // "is the server up" monitor. Documented rather than assumed, because the PostgreSQL checker
        // handles the same case completely differently — see PostgresCheckerE2E.
        var result = await ProbeAsync(database: "");

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task A_port_nothing_listens_on_is_Down()
    {
        var result = await ProbeAsync(port: Targets.TcpRefusedPort);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
    }

    [E2EFact]
    public async Task The_drivers_own_connect_timeout_bounds_a_blackholed_port()
    {
        // The database checkers are the two that do NOT rely on the caller's cancellation token to
        // bound themselves: both copy ctx.Timeout into the driver's ConnectionTimeout. So unlike TCP
        // and TLS, a blackholed port here produces a Down result rather than a cancellation — the
        // driver gives up on its own terms first.
        //
        // The cancellation budget is deliberately well above the connect timeout, so that if this ever
        // stopped being true the test would fail on the assertion rather than quietly turn into the
        // cancellation test it was written to distinguish itself from.
        var result = await ProbeAsync(
            port: Targets.TcpBlackholePort,
            timeout: TimeSpan.FromSeconds(3));

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
    }

    [E2ETheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_unconfigured_monitor_says_so_instead_of_connecting(string host)
    {
        var result = await ProbeAsync(host: host);

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Equal("Host not configured", result.Message);
        Assert.Null(result.ResponseTimeMs);
    }

    [E2EFact]
    public async Task Break_and_restore_moves_the_server_Down_and_back()
    {
        var before = await ProbeAsync();
        Assert.Equal(CheckStatus.Up, before.Status);

        using (var broken = TargetControl.Break(Target.MySql))
        {
            var during = await ProbeAsync(timeout: TimeSpan.FromSeconds(5));
            Assert.Equal(CheckStatus.Down, during.Status);

            broken.RestoreNow();

            // mysqld accepts connections a moment before it finishes its own startup, so the first
            // probe after a restore can still be refused. The helper waits for the port, which is as
            // much as it can observe from outside; this retries briefly for the rest.
            var after = await RetryUntilUpAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(CheckStatus.Up, after.Status);
        }
    }

    private async Task<CheckResult> RetryUntilUpAsync(TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        CheckResult result;
        do
        {
            result = await ProbeAsync();
            if (result.Status == CheckStatus.Up) return result;
            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);

        return result;
    }
}
