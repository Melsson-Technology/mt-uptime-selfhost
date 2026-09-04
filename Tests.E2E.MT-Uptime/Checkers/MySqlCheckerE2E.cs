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

    [E2ETheory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public async Task VerifyFull_is_refused_even_though_the_certificate_is_valid(string host)
    {
        // A PRODUCT LIMITATION, found on the first real box and asserted as it behaves.
        //
        // VerifyFull cannot connect to this server. VerifyCa can, on the same certificate, over the
        // same connection. So can MySQL's own client in --ssl-mode=VERIFY_IDENTITY, which is the
        // strictest mode it offers. So can Npgsql's VerifyFull, against a certificate minted by the
        // identical call from the identical CA.
        //
        // Everything checkable about the certificate is right: `openssl verify -purpose sslserver`
        // passes, the SAN carries DNS:localhost and IP:127.0.0.1, the EKU is serverAuth, the CA is in
        // the system trust store — and .NET itself accepts that CA elsewhere in this very tier, in
        // A_trusted_certificate_is_Up_over_HTTPS.
        //
        // What the product now reports, thanks to ProbeFailure.Describe:
        //
        //     SSL Authentication Error — The remote certificate was rejected due to the following
        //     error: RemoteCertificateChainErrors
        //
        // CHAIN errors, not name errors — which is why this is a theory over both spellings and both
        // fail identically. The one structural difference found between this and every validation
        // that succeeds: mysqld sends TWO certificates in the handshake (leaf + its own CA, because
        // `ssl-ca` is configured) where nginx sends one. That is a correlation, not a proof — .NET
        // reports SslPolicyErrors granularity and the specific X509ChainStatus is not recoverable
        // from the exception — so it is recorded as the leading hypothesis rather than as the cause.
        //
        // Why this matters to a user rather than only to us: the editor describes VerifyFull as "the
        // only mode that resists an on-path attacker, and the right choice for any database reached
        // over a network you do not control". A private CA is the usual reason to need it. If it does
        // not work against a MySQL server configured with ssl-ca — a common configuration — then the
        // mode that the product recommends most strongly is the one that fails.
        //
        // The next step, deliberately not taken here because it perturbs the box mid-run: comment out
        // `ssl-ca` in the server's config and re-run. If VerifyFull then connects, the hypothesis is
        // confirmed and the question becomes what MT-Uptime should do about it.
        //
        // Asserted as it behaves so the day it changes, this fails and gets rewritten — the same way
        // HttpCheckerE2E's certificate-message assertion was flipped once the reason stopped being
        // discarded.
        await AssertVerifyFullRefusedAsync(host);
    }

    private async Task AssertVerifyFullRefusedAsync(string host)
    {
        var result = await ProbeAsync(host: host, tls: DbTlsMode.VerifyFull);

        // Down, and soft — a handshake failure is a transport problem, not the server's verdict on
        // itself, so it must still burn the retry window rather than confirm an outage at once.
        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);

        // The message must name the CHAIN, and this is the assertion that pins the finding. If it
        // ever says NameMismatch instead, the cause is something else entirely and the comment above
        // is wrong. If it becomes Up, the limitation is gone and this test should be rewritten to
        // assert that.
        Assert.Contains("RemoteCertificateChainErrors", result.Message,
            StringComparison.Ordinal);

        // And the counterpart, in the same test, on the same connection: VerifyCa succeeds. Without
        // this the test would also pass on a box where MySQL was simply unreachable, which is exactly
        // the kind of hollow assertion this battery exists to avoid.
        var verifyCa = await ProbeAsync(host: host, tls: DbTlsMode.VerifyCa);
        Assert.True(verifyCa.Status == CheckStatus.Up,
            $"VerifyCa against '{host}' should still connect, but was {verifyCa.Status}: {verifyCa.Message}");
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
    public async Task An_unknown_database_reports_access_denied_rather_than_naming_it_as_missing()
    {
        // CORRECTED ON THE BOX. The plan predicted "Unknown database". A real MySQL answers
        //
        //     Access denied for user 'e2e_probe'@'127.0.0.1' to database 'no_such_database_e2e'
        //
        // and that is correct behaviour rather than a quirk: `e2e_probe` is granted only on `e2e.*`,
        // and MySQL deliberately does not distinguish "that database does not exist" from "you may
        // not see it" for an unprivileged user. Telling them apart would let any account with a login
        // enumerate every schema on the server.
        //
        // Worth a test of its own because of what an operator sees: typo a database name on a
        // least-privileged monitor and the alert says your CREDENTIALS are wrong. That sends them to
        // re-check a password that was never the problem.
        var result = await ProbeAsync(database: "no_such_database_e2e");

        Assert.Equal(CheckStatus.Down, result.Status);
        Assert.False(result.Hard);
        Assert.Contains("Access denied", result.Message, StringComparison.OrdinalIgnoreCase);

        // The one thing that distinguishes this from a wrong password: the message names the database
        // rather than saying "using password: YES". MySQL error 1044, not 1045.
        Assert.Contains("no_such_database_e2e", result.Message, StringComparison.Ordinal);
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
