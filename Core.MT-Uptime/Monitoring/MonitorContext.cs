namespace MT.Uptime.Core.Monitoring;

/// <summary>Everything a checker needs for one run — a snapshot, so checkers never touch the database.</summary>
public sealed record MonitorContext(
    int MonitorId,
    string Name,
    MonitorType Type,
    TimeSpan Timeout,
    string ConfigJson);
