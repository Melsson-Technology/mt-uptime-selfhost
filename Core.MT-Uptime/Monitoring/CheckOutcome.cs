namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// A fully-decided check result queued to the single-writer <see cref="HeartbeatWriter"/>,
/// carrying the heartbeat, the denormalized status update, and any event open/resolve.
/// </summary>
public sealed record CheckOutcome(
    int MonitorId,
    DateTime Timestamp,
    MonitorStatus HeartbeatStatus,
    double? ResponseTimeMs,
    string? StatusCode,
    string? Message,
    bool Important,
    int Attempt,
    DateTime? CertExpiresAt,
    EventAction EventAction,
    MonitorStatus FromStatus,
    MonitorStatus ToStatus)
{
    /// <summary>
    /// Serialized failure diagnostics for this beat, or null. An init property so the record's shape is
    /// unchanged for every caller that has nothing to add.
    /// </summary>
    public string? Diagnostics { get; init; }
}
