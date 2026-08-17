namespace MT.Uptime.Core.Monitoring.Configs;

/// <summary>Settings for a standalone TLS certificate-expiry monitor.</summary>
public sealed class TlsMonitorConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 443;

    /// <summary>Report Down when the certificate is within this many days of expiring.</summary>
    public int WarnDays { get; set; } = 14;
}
