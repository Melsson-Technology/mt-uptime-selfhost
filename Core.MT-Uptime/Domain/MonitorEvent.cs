namespace MT.Uptime.Core.Domain;

/// <summary>
/// A state-change / incident record (low volume). Powers the event log and downtime durations
/// directly, so they don't have to be re-derived from the heartbeat stream.
/// </summary>
public class MonitorEvent
{
    public long Id { get; set; }

    public int MonitorId { get; set; }
    public Monitor? Monitor { get; set; }

    public MonitorStatus FromStatus { get; set; }
    public MonitorStatus ToStatus { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Duration of the state that started here, set when the event is resolved.</summary>
    public long? DurationSeconds { get; set; }

    public string? Reason { get; set; }

    /// <summary>
    /// The <see cref="Domain.Incident"/> this event belongs to. Nullable because every event written before
    /// incidents existed has none, and because grouping is a layer above this record rather than part of
    /// it — the event's own open/resolve semantics are unchanged either way.
    /// </summary>
    public long? IncidentId { get; set; }
    public Incident? Incident { get; set; }
}
