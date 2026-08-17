namespace MT.Uptime.Core.Domain;

/// <summary>
/// The result of one check. High-volume table (pruned by the retention job); indexed by
/// (MonitorId, Timestamp) for detail queries and pruning.
/// </summary>
public class Heartbeat
{
    public long Id { get; set; }

    public int MonitorId { get; set; }
    public Monitor? Monitor { get; set; }

    public DateTime Timestamp { get; set; }
    public MonitorStatus Status { get; set; }
    public double? ResponseTimeMs { get; set; }

    /// <summary>Protocol-specific code (e.g. HTTP status). Free-form string to fit every monitor type.</summary>
    public string? StatusCode { get; set; }
    public string? Message { get; set; }

    /// <summary>True when this heartbeat marks a state transition (drives the event log and notifications).</summary>
    public bool Important { get; set; }

    /// <summary>Retry attempt index within the current failure streak (0 = first failure).</summary>
    public int Attempt { get; set; }

    /// <summary>
    /// True when a <see cref="MaintenanceWindow"/> covered this monitor at the moment of the check.
    /// <para>
    /// <see cref="Status"/> is still the truth — a target that was down during maintenance records Down,
    /// and the heartbeat bar shows it. This flag only removes the beat from the uptime calculation, on
    /// both sides of the fraction: planned work is neither uptime nor downtime, it is excluded.
    /// </para>
    /// </summary>
    public bool Maintenance { get; set; }
}
