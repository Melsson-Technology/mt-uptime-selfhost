namespace MT.Uptime.Core.Domain;

/// <summary>
/// A label applied to monitors — an environment, a customer, a host, a service.
/// <para>
/// Deliberately a first-class row rather than a comma-separated string on <see cref="Monitor"/>.
/// Free-text tags drift ("prod", "Prod", "production") until filtering by one silently misses half the
/// monitors it should match, and there is nowhere to hang a colour. A row also gives later features
/// something to join to: per-client status pages and rollups are "the monitors carrying this tag".
/// </para>
/// </summary>
public class Tag
{
    public int Id { get; set; }

    /// <summary>Display name. Unique case-insensitively, so "Prod" and "prod" cannot both exist.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Hex colour (<c>#RRGGBB</c>) for the chip. Stored rather than derived from the name so a rename
    /// does not silently recolour a tag someone has learned to recognise at a glance.
    /// </summary>
    public string Colour { get; set; } = DefaultColour;

    public const string DefaultColour = "#6B7280";

    public DateTime CreatedAt { get; set; }

    // --- Navigation ---
    public ICollection<MonitorTag> Monitors { get; set; } = new List<MonitorTag>();
}

/// <summary>Join entity: which monitors carry which tags.</summary>
public class MonitorTag
{
    public int MonitorId { get; set; }
    public Monitor? Monitor { get; set; }

    public int TagId { get; set; }
    public Tag? Tag { get; set; }
}
