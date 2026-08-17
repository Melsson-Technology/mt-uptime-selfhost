namespace MT.Uptime.Core.Monitoring;

/// <summary>The latest in-memory status of one monitor, pushed to the dashboard over the Blazor circuit.</summary>
public sealed record MonitorLiveState
{
    public required int MonitorId { get; init; }
    public required string Name { get; init; }
    public MonitorType Type { get; init; }
    public bool Enabled { get; init; }
    public MonitorStatus Status { get; init; } = MonitorStatus.Pending;
    public DateTime? LastCheckAt { get; init; }
    public double? LastResponseMs { get; init; }
    public string? Message { get; init; }
    public DateTime? CertExpiresAt { get; init; }

    /// <summary>Recent heartbeat statuses (oldest→newest) for a compact live sparkline on the dashboard.</summary>
    public IReadOnlyList<MonitorStatus> Recent { get; init; } = [];
}
