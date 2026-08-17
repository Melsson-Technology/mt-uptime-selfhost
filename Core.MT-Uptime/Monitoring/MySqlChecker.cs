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

        var builder = new MySqlConnectionStringBuilder
        {
            Server = cfg.Host,
            Port = (uint)(cfg.Port > 0 ? cfg.Port : 3306),
            Database = cfg.Database ?? string.Empty,
            UserID = cfg.Username ?? string.Empty,
            Password = Reveal(cfg.Password) ?? string.Empty,
            ConnectionTimeout = (uint)Math.Clamp((int)ctx.Timeout.TotalSeconds, 1, 300),
        };

        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new MySqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            sw.Stop();
            return CheckResult.Up(sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { sw.Stop(); return CheckResult.Down(ex.Message, sw.Elapsed.TotalMilliseconds); }
    }

    private string? Reveal(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return cipher;
        try { return protector.Unprotect(cipher); }
        catch { return null; }
    }

    private static DbMonitorConfig Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<DbMonitorConfig>(json) ?? new(); }
        catch { return new(); }
    }
}
