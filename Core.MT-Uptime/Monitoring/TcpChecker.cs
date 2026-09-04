using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using MT.Uptime.Core.Monitoring.Configs;

namespace MT.Uptime.Core.Monitoring;

/// <summary>Checks that a TCP port accepts a connection within the timeout.</summary>
public sealed class TcpChecker : IMonitorChecker
{
    public MonitorType Type => MonitorType.Tcp;

    public async Task<CheckResult> CheckAsync(MonitorContext ctx, CancellationToken ct)
    {
        var cfg = Deserialize(ctx.ConfigJson);
        if (string.IsNullOrWhiteSpace(cfg.Host) || cfg.Port is <= 0 or > 65535)
            return CheckResult.Down("Host/port not configured");

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(cfg.Host, cfg.Port, ct);
            sw.Stop();
            return CheckResult.Up(sw.Elapsed.TotalMilliseconds, $"{cfg.Host}:{cfg.Port}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { sw.Stop(); return CheckResult.Down(ProbeFailure.Describe(ex), sw.Elapsed.TotalMilliseconds); }
    }

    private static TcpMonitorConfig Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<TcpMonitorConfig>(json) ?? new(); }
        catch { return new(); }
    }
}
