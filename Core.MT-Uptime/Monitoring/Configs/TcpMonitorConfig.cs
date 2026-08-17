namespace MT.Uptime.Core.Monitoring.Configs;

/// <summary>Settings for a TCP port monitor, serialized into <c>Monitor.ConfigJson</c>.</summary>
public sealed class TcpMonitorConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
}
