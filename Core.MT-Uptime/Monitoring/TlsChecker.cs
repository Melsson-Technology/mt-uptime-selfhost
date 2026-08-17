using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MT.Uptime.Core.Monitoring.Configs;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Watches a TLS certificate's expiry. Accepts any certificate during the handshake (so that
/// expired / near-expiry / self-signed certs are still readable) and reports Down when the cert
/// is expired or within the warn window, otherwise Up. <see cref="CheckResult.CertExpiresAt"/> is set.
/// </summary>
public sealed class TlsChecker : IMonitorChecker
{
    public MonitorType Type => MonitorType.Tls;

    public async Task<CheckResult> CheckAsync(MonitorContext ctx, CancellationToken ct)
    {
        var cfg = Deserialize(ctx.ConfigJson);
        if (string.IsNullOrWhiteSpace(cfg.Host))
            return CheckResult.Down("Host not configured");

        var port = cfg.Port > 0 ? cfg.Port : 443;
        var warnDays = Math.Max(1, cfg.WarnDays);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(cfg.Host, port, ct);

            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = cfg.Host,
                RemoteCertificateValidationCallback = (_, _, _, _) => true, // read the cert even if invalid/expired
            }, ct);
            sw.Stop();

            if (ssl.RemoteCertificate is not X509Certificate2 cert)
                return CheckResult.Down("No certificate presented", sw.Elapsed.TotalMilliseconds);

            var notAfter = cert.NotAfter.ToUniversalTime();
            var daysLeft = (int)Math.Floor((notAfter - DateTime.UtcNow).TotalDays);
            var ms = sw.Elapsed.TotalMilliseconds;

            if (daysLeft < 0)
                return CheckResult.Down($"Certificate expired {-daysLeft}d ago", ms, "expired", notAfter);
            if (daysLeft <= warnDays)
                return CheckResult.Down($"Certificate expires in {daysLeft}d", ms, $"{daysLeft}d", notAfter);

            return CheckResult.Up(ms, $"{daysLeft}d", $"Valid for {daysLeft}d", notAfter);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { sw.Stop(); return CheckResult.Down(ex.Message, sw.Elapsed.TotalMilliseconds); }
    }

    private static TlsMonitorConfig Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<TlsMonitorConfig>(json) ?? new(); }
        catch { return new(); }
    }
}
