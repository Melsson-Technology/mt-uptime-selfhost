namespace MT.Uptime.Core.Monitoring.Configs;

/// <summary>Settings for a DNS monitor, serialized into <c>Monitor.ConfigJson</c>.</summary>
public sealed class DnsMonitorConfig
{
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Record type to query: A, AAAA, CNAME, MX, or TXT.</summary>
    public string RecordType { get; set; } = "A";

    /// <summary>Optional custom resolver IP (e.g. 8.8.8.8). Blank = use the system resolver.</summary>
    public string? Resolver { get; set; }

    /// <summary>Optional assertion: the lookup must return a value containing this string.</summary>
    public string? ExpectedValue { get; set; }
}
