namespace MT.Uptime.Core.Domain;

/// <summary>
/// One real-world failure, which may span several monitors. Sits <b>above</b> <see cref="MonitorEvent"/>
/// rather than replacing it: a <see cref="MonitorEvent"/> is still the per-monitor record of "this target
/// went down at T and came back at T+n", and the state machine still guarantees a monitor never has two
/// open events. An incident groups the events that share a piece of infrastructure, so a host carrying
/// twenty monitored sites produces one incident instead of twenty unrelated alerts.
/// <para>
/// A single-monitor outage is simply an incident with one member, so every path through the engine has an
/// incident — nothing has to special-case the uncorrelated case.
/// </para>
/// </summary>
public class Incident
{
    public long Id { get; set; }

    /// <summary>
    /// The shared infrastructure these events were grouped by — see <c>CorrelationKeyResolver</c>.
    /// Null when the monitor could not be resolved to anything (a push monitor, or a DNS monitor on the
    /// system resolver), in which case the incident stays single-monitor by construction.
    /// </summary>
    public string? CorrelationKey { get; set; }

    /// <summary>Short human label, taken from the first member's monitor name and widened as members join.</summary>
    public string Title { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When the most recent member event opened. Correlation is judged against this rather than
    /// <see cref="StartedAt"/> so a long-running incident does not stay open as a magnet, silently
    /// absorbing an unrelated failure on the same host days later.
    /// </summary>
    public DateTime LastEventAt { get; set; }

    public DateTime? ResolvedAt { get; set; }

    /// <summary>Duration of the incident, stamped when the last member event resolves.</summary>
    public long? DurationSeconds { get; set; }

    /// <summary>
    /// The worst status seen across the member events (<see cref="MonitorStatus.Down"/> outranks
    /// <see cref="MonitorStatus.Degraded"/>). Escalates as members join and is never walked back while
    /// the incident is open — an incident that was ever a full outage is reported as one.
    /// </summary>
    public MonitorStatus Severity { get; set; }

    /// <summary>How many monitors have joined this incident, denormalized so lists need no group-by.</summary>
    public int MonitorCount { get; set; }

    // --- Acknowledgement (behaviour wired in E3b; the columns live here so there is one schema change) ---

    /// <summary>
    /// Set when an operator acknowledges the incident. Acknowledgement is deliberately per-incident and
    /// never per-monitor: during a correlated outage, acking a monitor would either silence one alert of
    /// twenty or silence that monitor for good.
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }

    public int? AcknowledgedByUserId { get; set; }
    public AppUser? AcknowledgedBy { get; set; }

    /// <summary>Suppress repeat alerts for this incident until this time. Null = not snoozed.</summary>
    public DateTime? SnoozedUntil { get; set; }

    /// <summary>
    /// Whether this incident may appear on status pages carrying an affected monitor.
    /// <para>
    /// Defaults to true. A status page that stays green through an outage its own monitors are reporting
    /// is worse than useless, so publishing is the default and hiding is the deliberate act. Only monitors
    /// actually listed on a given page are ever named on it — see <c>PublicIncident</c>.
    /// </para>
    /// </summary>
    public bool Published { get; set; } = true;

    public ICollection<MonitorEvent> Events { get; set; } = new List<MonitorEvent>();
    public ICollection<IncidentUpdate> Updates { get; set; } = new List<IncidentUpdate>();

    public bool IsOpen => ResolvedAt is null;
}
