using System.Diagnostics;
using System.Text.Json;
using MT.Uptime.Core.Monitoring.Configs;
using MT.Uptime.Core.Security;
using MySqlConnector;

namespace MT.Uptime.Core.Monitoring;

/// <summary>Opens a MySQL connection and runs a lightweight <c>SELECT 1</c> probe.</summary>
public sealed class MySqlChecker(ISecretProtector protector) : IMonitorChecker
{
    public MonitorType Type => MonitorType.MySql;

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
            var builder = new MySqlConnectionStringBuilder
            {
                Server = cfg.Host,
                Port = (uint)(cfg.Port > 0 ? cfg.Port : 3306),
                Database = cfg.Database ?? string.Empty,
                UserID = cfg.Username ?? string.Empty,
                Password = Reveal(cfg.Password) ?? string.Empty,
                ConnectionTimeout = (uint)Math.Clamp((int)ctx.Timeout.TotalSeconds, 1, 300),
                // Stated explicitly rather than left to the driver's default, so the connection's
                // protection is visible here and is the operator's choice. See DbTlsMode on why the
                // default is weak.
                SslMode = cfg.Tls switch
                {
                    DbTlsMode.Required => MySqlSslMode.Required,
                    DbTlsMode.VerifyCa => MySqlSslMode.VerifyCA,
                    DbTlsMode.VerifyFull => MySqlSslMode.VerifyFull,
                    _ => MySqlSslMode.Preferred,
                },
            };

            await using var conn = new MySqlConnection(builder.ConnectionString);
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
            // distinctly because the alternative — connecting with a blank password — makes the server
            // answer "Access denied … (using password: NO)", sending the operator after a database that
            // is perfectly healthy when the fault is on this side.
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
