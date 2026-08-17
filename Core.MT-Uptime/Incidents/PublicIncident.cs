namespace MT.Uptime.Core.Incidents;

/// <summary>One operator note, as shown publicly.</summary>
public sealed record PublicIncidentUpdate(IncidentUpdateKind Kind, string Body, DateTime PostedAt);

/// <summary>
/// An incident reduced to what a specific status page is allowed to say about it.
/// <para>
/// <b>This type exists to stop a leak.</b> An incident groups monitors by shared infrastructure, which
/// routinely means monitors belonging to different customers and appearing on different status pages —
/// and <see cref="Incident.Title"/> is taken from whichever monitor failed first, which may be on none of
/// them. Rendering the entity on a public page would therefore publish the names of unrelated,
/// unlisted monitors. Building this projection per page, from that page's own monitor list, is what
/// prevents it, so public pages must never be handed an <see cref="Incident"/> directly.
/// </para>
/// </summary>
public sealed record PublicIncident(
    long Id,
    MonitorStatus Severity,
    DateTime StartedAt,
    DateTime? ResolvedAt,
    IReadOnlyList<string> AffectedMonitors,
    IReadOnlyList<PublicIncidentUpdate> Updates)
{
    public bool IsOpen => ResolvedAt is null;

    /// <summary>
    /// Headline composed from the monitors this page actually lists — never from the incident's own
    /// title, which names the first monitor to fail anywhere.
    /// </summary>
    public string Headline => AffectedMonitors.Count switch
    {
        0 => "Service disruption",
        1 => AffectedMonitors[0],
        2 => $"{AffectedMonitors[0]} and {AffectedMonitors[1]}",
        _ => $"{AffectedMonitors[0]} and {AffectedMonitors.Count - 1} others",
    };
}

/// <summary>A maintenance window as announced on a status page.</summary>
public sealed record PublicMaintenance(
    string Name,
    string? Description,
    DateTime StartsAt,
    DateTime EndsAt,
    bool InProgress);
