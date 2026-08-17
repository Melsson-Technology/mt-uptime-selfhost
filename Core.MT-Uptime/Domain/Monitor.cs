namespace MT.Uptime.Core.Domain;

/// <summary>
/// A single thing being watched (a URL, host:port, database, DNS record, or TLS endpoint).
/// Type-specific settings live in <see cref="ConfigJson"/> so new monitor types need no schema change.
/// </summary>
public class Monitor
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public MonitorType Type { get; set; }

    /// <summary>How often to run the check.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>How long a single check may run before it is treated as a failure.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Consecutive failures required before the monitor is marked Down (0 = alert on first failure).</summary>
    public int RetryCount { get; set; }

    /// <summary>Re-send the "still down" alert every N failed checks while down (0 = never re-send).</summary>
    public int ResendEveryN { get; set; }

    /// <summary>Invert the result: a passing check counts as Down and vice-versa.</summary>
    public bool UpsideDown { get; set; }

    /// <summary>
    /// Response time (ms) above which a successful check counts as <see cref="MonitorStatus.Degraded"/>.
    /// Null or 0 disables degraded detection entirely, which is the default — an HTTP 200 that takes
    /// eight seconds is a real problem, but only the operator knows what "too slow" means for a target.
    /// </summary>
    public int? SlowThresholdMs { get; set; }

    /// <summary>
    /// Consecutive slow checks required before Degraded is confirmed and alerted. Mirrors
    /// <see cref="RetryCount"/> for outages, and exists because response time is spiky: a single slow
    /// sample is usually noise, three in a row is a trend.
    /// </summary>
    public int DegradedAfterChecks { get; set; } = 3;

    public bool Enabled { get; set; } = true;

    /// <summary>Per-type configuration serialized as JSON (deserialized by the matching checker).</summary>
    public string ConfigJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // --- Denormalized live-status cache (kept current by the engine for fast dashboard rendering) ---
    public MonitorStatus CurrentStatus { get; set; } = MonitorStatus.Pending;
    public DateTime? LastHeartbeatAt { get; set; }
    public double? LastResponseTimeMs { get; set; }
    public DateTime? CertExpiresAt { get; set; }

    // --- Navigation ---
    public ICollection<Heartbeat> Heartbeats { get; set; } = new List<Heartbeat>();
    public ICollection<MonitorEvent> Events { get; set; } = new List<MonitorEvent>();
    public ICollection<MonitorNotification> Notifications { get; set; } = new List<MonitorNotification>();
    public ICollection<StatusPageMonitor> StatusPageLinks { get; set; } = new List<StatusPageMonitor>();
    public ICollection<MonitorTag> Tags { get; set; } = new List<MonitorTag>();
}
