using System.Diagnostics;
using System.Text.Json;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Core.Security;
using Npgsql;

namespace MT.Uptime.Core.Monitoring;

/// <summary>Opens a PostgreSQL connection and runs a lightweight <c>SELECT 1</c> probe.</summary>
public sealed class PostgresChecker(ISecretProtector protector) : IMonitorChecker
{
    public MonitorType Type => MonitorType.Postgres;

    public async Task<CheckResult> CheckAsync(MonitorContext ctx, CancellationToken ct)
    {
        var cfg = Deserialize(ctx.ConfigJson);
        if (string.IsNullOrWhiteSpace(cfg.Host))
            return CheckResult.Down("Host not configured");

        var sw = Stopwatch.StartNew();
        try
        {
            // Built inside the try because Reveal throws now: a password the key ring cannot read has to
            // come back as a Down result against this monitor rather than escape into the scheduler.
            // Same reasoning as HttpChecker.BuildRequest.
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = cfg.Host,
                Port = cfg.Port > 0 ? cfg.Port : 5432,
                Database = string.IsNullOrWhiteSpace(cfg.Database) ? "postgres" : cfg.Database,
                Username = cfg.Username ?? string.Empty,
                Password = Reveal(cfg.Password) ?? string.Empty,
                Timeout = Math.Clamp((int)ctx.Timeout.TotalSeconds, 1, 300),
                // Stated explicitly rather than left to the driver's default, so the connection's
                // protection is visible here and is the operator's choice. See DbTlsMode on why the
                // default is weak.
                SslMode = cfg.Tls switch
                {
                    DbTlsMode.Required => SslMode.Require,
                    DbTlsMode.VerifyCa => SslMode.VerifyCA,
                    DbTlsMode.VerifyFull => SslMode.VerifyFull,
                    _ => SslMode.Prefer,
                },
            };

            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            sw.Stop();
            return CheckResult.Up(sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (SecretUnreadableException ex)
        {
            // Ahead of the general catch, which would otherwise report this as an ordinary soft Down.
            // Retrying cannot bring the key ring back, so confirm Down at once; and it is reported
            // distinctly because the alternative — a blank password — makes Npgsql give up with "No
            // password has been provided", which reads as a monitor nobody finished configuring rather
            // than a key ring that stopped working.
            sw.Stop();
            return CheckResult.Down(ex.Message, sw.Elapsed.TotalMilliseconds, hard: true);
        }
        catch (Exception ex) { sw.Stop(); return CheckResult.Down(ex.Message, sw.Elapsed.TotalMilliseconds); }
    }

    /// <summary>Decrypts the stored password, or throws <see cref="SecretUnreadableException"/>.</summary>
    private string? Reveal(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return cipher;
        try { return protector.Unprotect(cipher); }
        catch (Exception ex)
        {
            throw new SecretUnreadableException(
                "This monitor's stored database password could not be decrypted — the Data Protection " +
                "key ring is missing or does not match the database. See deploy/README-deploy.md.", ex);
        }
    }

    private static DbMonitorConfig Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<DbMonitorConfig>(json) ?? new(); }
        catch { return new(); }
    }
}
