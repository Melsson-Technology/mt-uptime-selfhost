using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Core.Security;
using MT.Uptime.Tests.E2E.Support;

namespace MT.Uptime.Tests.E2E.Checkers;

/// <summary>
/// <see cref="PostgresChecker"/> against a real PostgreSQL, with <c>ssl = on</c> and a certificate
/// from the same locally-minted CA MySQL uses.
/// <para>
/// Deliberately a near-mirror of <see cref="MySqlCheckerE2E"/>, because the two checkers are
/// near-mirrors of each other and share a config class — and the interesting result is where they
/// stop matching. There is one such place, and it is the blank-database case: MySQL connects without
/// selecting a schema, while PostgreSQL substitutes the <c>postgres</c> database. Same field, same
/// empty value, two different targets being monitored.
/// </para>
/// </summary>
public class PostgresCheckerE2E : IClassFixture<CheckerHost>
{
    private readonly IMonitorChecker _postgres;
    private readonly ISecretProtector _protector;

    public PostgresCheckerE2E(CheckerHost host)
    {
        _postgres = host.For(MonitorType.Postgres);
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
        => Probe.RunAsync(_postgres, Probe.Context(MonitorType.Postgres, new DbMonitorConfig
        {
            Host = host ?? Targets.PostgresHost,
            Port = port ?? Targets.PostgresPort,
            Database = database ?? Targets.PostgresDatabase,
            Username = username ?? Targets.PostgresUser,
            Password = _protector.Protect(password ?? Targets.PostgresPassword),
            Tls = tls,
        }, timeout ?? TimeSpan.FromSeconds(15)));

    [E2ETheory]
    [InlineData(DbTlsMode.Preferred)]
    [InlineData(DbTlsMode.Required)]
    [InlineData(DbTlsMode.VerifyCa)]
    public async Task Every_TLS_mode_up_to_VerifyCa_connects(DbTlsMode tls)
    {
        var result = await ProbeAsync(tls: tls);

        Assert.Equal(CheckStatus.Up, result.Status);
        Assert.NotNull(result.ResponseTimeMs);
        Assert.Null(result.StatusCode);
        Assert.Null(result.Message);
    }

    [E2EFact]
    public async Task VerifyFull_connects_when_the_host_matches_the_certificate()
    {
        var result = await ProbeAsync(host: "localhost", tls: DbTlsMode.VerifyFull);

        Assert.Equal(CheckStatus.Up, result.Status);
    }

    [E2EFact]
    public async Task A_wrong_password_is_a_soft_Down_naming_the_failure()
    {
        var result = await ProbeAsync(password: "definitely-not-the-password");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
        Assert.Contains("password authentication failed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task An_unknown_database_is_Down()
    {
        var result = await ProbeAsync(database: "no_such_database_e2e");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.Contains("does not exist", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [E2EFact]
    public async Task A_blank_database_silently_becomes_the_postgres_database()
    {
        // THE ONE PLACE THE TWO DATABASE CHECKERS DISAGREE, and it is worth knowing about.
        //
        // PostgresChecker substitutes "postgres" when the field is blank. So a monitor saved with no
        // database reports Up on the strength of a database the operator never named — and it will
        // keep reporting Up after the database they actually cared about has been dropped. MySQL's
        // blank-database behaviour is genuinely "just check the server"; this one looks the same and
        // is not.
        //
        // Proven rather than asserted from the source: the same connection with the substitution made
        // explicit has to behave identically.
        var blank = await ProbeAsync(database: "");
        var explicitly = await ProbeAsync(database: "postgres");

        Assert.Equal(CheckStatus.Up, blank.Status);
        Assert.Equal(CheckStatus.Up, explicitly.Status);

        // And the negative half: if the substitution were not happening, naming a database that does
        // not exist would behave the same as leaving it blank. It does not.
        var missing = await ProbeAsync(database: "no_such_database_e2e");
        Assert.Equal(CheckStatus.Down, missing.Status);
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

        using (var broken = TargetControl.Break(Target.Postgres))
        {
            var during = await ProbeAsync(timeout: TimeSpan.FromSeconds(5));
            Assert.Equal(CheckStatus.Down, during.Status);

            broken.RestoreNow();

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
