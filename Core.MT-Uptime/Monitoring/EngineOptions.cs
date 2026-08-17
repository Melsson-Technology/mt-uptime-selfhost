namespace MT.Uptime.Core.Monitoring;

/// <summary>Engine tunables, bound from the "Engine" configuration section.</summary>
public sealed class EngineOptions
{
    /// <summary>Hard cap on concurrent checks. 0 = auto: clamp(cores * 4, 8, 32).</summary>
    public int MaxConcurrentChecks { get; set; }

    /// <summary>
    /// Default days of raw <see cref="Domain.Heartbeat"/> history to keep before pruning.
    /// Overridable at runtime via the "Retention:RawDays" setting; this is the fallback.
    /// </summary>
    public int RawRetentionDays { get; set; } = 30;

    /// <summary>Days of hourly <see cref="Domain.StatRollup"/> buckets to keep. Daily buckets are kept indefinitely.</summary>
    public int HourlyRetentionDays { get; set; } = 180;

    /// <summary>
    /// How long an open <see cref="Domain.Incident"/> keeps accepting new monitors, measured from its most
    /// recent member rather than its start. Ten minutes comfortably covers a host dying and taking every
    /// monitor on it down over their next check, including monitors on a five-minute interval, without
    /// leaving a long-running incident open as a magnet for unrelated later failures.
    /// </summary>
    public int IncidentCorrelationWindowMinutes { get; set; } = 10;

    public int ResolveMaxConcurrency()
        => MaxConcurrentChecks > 0 ? MaxConcurrentChecks : Math.Clamp(Environment.ProcessorCount * 4, 8, 32);
}
