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
    /// Days to keep a <b>resolved</b> <see cref="Domain.Incident"/> and its updates. Open incidents are
    /// never pruned, whatever their age — an outage still running is not history.
    /// <para>
    /// A year, which is far longer than the 30-day raw heartbeat window on purpose. An incident is a
    /// handful of small rows describing something that mattered, and "what broke last October" is a
    /// question people actually ask; a heartbeat is one row of a firehose. The number that governs disk
    /// is <see cref="RawRetentionDays"/>, so there is no reason to be stingy here.
    /// </para>
    /// <para>
    /// Pruning an incident does not discard the per-monitor history it grouped:
    /// <c>MonitorEvent.IncidentId</c> is <c>SetNull</c> rather than <c>Cascade</c>, so the events remain
    /// and only the grouping goes. <see cref="Domain.IncidentUpdate"/> does cascade, because an operator
    /// note about an incident has no meaning once the incident is gone.
    /// </para>
    /// </summary>
    public int IncidentRetentionDays { get; set; } = 365;

    /// <summary>
    /// How long an open <see cref="Domain.Incident"/> keeps accepting new monitors, measured from its most
    /// recent member rather than its start. Ten minutes comfortably covers a host dying and taking every
    /// monitor on it down over their next check, including monitors on a five-minute interval, without
    /// leaving a long-running incident open as a magnet for unrelated later failures.
    /// </summary>
    public int IncidentCorrelationWindowMinutes { get; set; } = 10;

    /// <summary>
    /// The instance's public origin, e.g. <c>https://uptime.example.com</c>, used to build the
    /// "full diagnostics" link in alerts sent to channels too small to carry the detail inline.
    /// <para>
    /// Bound by the host from the same <c>App:PublicBaseUrl</c> setting that emailed links use, rather
    /// than from a second key, so an operator configures the origin once. Null or unset simply means
    /// those alerts carry no link — never a broken one built from a guess.
    /// </para>
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Whether <see cref="RetentionService"/>'s daily timer runs in this process. Default true, which is
    /// right for any install that owns its own database.
    /// <para>
    /// <b>Turn it off wherever several processes share one database.</b> Retention reaches for raw SQL
    /// on purpose — a set-based rollup that never materialises a heartbeat, and a bounded delete whose
    /// write lock stays short — and raw SQL sees the whole table. It has no notion of which rows belong
    /// to whom, and nothing above it can give it one: a global query filter does not apply to a command
    /// the caller wrote by hand. So N processes over one database means each of them prunes <i>all</i> of
    /// it, on whichever retention window that process happens to be configured with, and writes every
    /// other process's rollups into its own rows.
    /// </para>
    /// <para>
    /// Setting this false does not make retention happen elsewhere. Such a deployment still owes its
    /// data a cleanup, and owes it exactly once, centrally, from something that is allowed to see
    /// everything — <see cref="RetentionService.RunCleanupAsync"/> is public and callable for that.
    /// Leaving this true is the option that loses other people's data.
    /// </para>
    /// </summary>
    public bool RunRetention { get; set; } = true;

    public int ResolveMaxConcurrency()
        => MaxConcurrentChecks > 0 ? MaxConcurrentChecks : Math.Clamp(Environment.ProcessorCount * 4, 8, 32);
}
