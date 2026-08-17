namespace MT.Uptime.Core.Domain;

/// <summary>
/// Where an incident has got to, using the vocabulary status pages have settled on so readers do not have
/// to learn ours. Ordered by progress; the numbers are persisted, so do not renumber them.
/// </summary>
public enum IncidentUpdateKind
{
    Investigating = 0,
    Identified = 1,
    Monitoring = 2,
    Resolved = 3,
}

/// <summary>
/// An operator's note on an <see cref="Incident"/>, shown on any status page carrying an affected monitor.
/// <para>
/// This is the whole of the "status page incidents" feature: the incident itself, its severity and its
/// timing already exist and are derived from monitoring. What a status page adds that a monitor list
/// cannot is a human saying what happened — so that is the only thing stored here.
/// </para>
/// </summary>
public class IncidentUpdate
{
    public long Id { get; set; }

    public long IncidentId { get; set; }
    public Incident? Incident { get; set; }

    public IncidentUpdateKind Kind { get; set; }

    public string Body { get; set; } = string.Empty;

    public DateTime PostedAt { get; set; }

    public int? PostedByUserId { get; set; }
    public AppUser? PostedBy { get; set; }
}
