namespace MT.Uptime.Core.Domain;

/// <summary>
/// A pre-aggregated bucket of heartbeat outcomes for one monitor and one time window
/// (<see cref="RollupPeriod.Hourly"/> or <see cref="RollupPeriod.Daily"/>). Written by the
/// retention job before raw <see cref="Heartbeat"/> rows are pruned, so long-range uptime %
/// survives even after the raw history is deleted. Unique per (MonitorId, Period, BucketStart).
/// </summary>
public class StatRollup
{
    public long Id { get; set; }

    public int MonitorId { get; set; }
    public Monitor? Monitor { get; set; }

    public RollupPeriod Period { get; set; }

    /// <summary>Start of the bucket in UTC, floored to the period (hour or day).</summary>
    public DateTime BucketStart { get; set; }

    public int UpCount { get; set; }
    public int DownCount { get; set; }
    public int PendingCount { get; set; }

    /// <summary>
    /// Beats that were up but slower than the monitor's threshold. Counted separately from
    /// <see cref="UpCount"/> so degraded periods stay visible in history, but treated as available
    /// when computing uptime % (see <c>MonitorStatsService</c>) — the target was answering.
    /// </summary>
    public int DegradedCount { get; set; }

    /// <summary>
    /// Beats taken while a maintenance window was open.
    /// <para>
    /// <b>Counted outside the four status buckets, not inside them.</b> A maintenance beat is excluded
    /// from <see cref="UpCount"/>, <see cref="DownCount"/>, <see cref="PendingCount"/> and
    /// <see cref="DegradedCount"/> entirely, so <see cref="Total"/> is already the uptime denominator with
    /// maintenance removed. Counting them inside and subtracting later would double-count: a maintenance
    /// beat that happened to be up would sit in the numerator and be removed from the denominator at the
    /// same time, pushing the reported percentage above 100.
    /// </para>
    /// </summary>
    public int MaintenanceCount { get; set; }

    /// <summary>
    /// Total beats counted toward uptime (the denominator), including pending and degraded but excluding
    /// maintenance — see <see cref="MaintenanceCount"/>.
    /// </summary>
    public int Total => UpCount + DownCount + PendingCount + DegradedCount;

    /// <summary>Beats that count as available: fully up plus degraded-but-answering.</summary>
    public int AvailableCount => UpCount + DegradedCount;

    public double? PingAvgMs { get; set; }
    public double? PingMinMs { get; set; }
    public double? PingMaxMs { get; set; }
}
