namespace MT.Uptime.Core.Domain;

/// <summary>A public, read-only status page reachable at /status/{slug} without authentication.</summary>
public class StatusPage
{
    public int Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Published { get; set; }
    public string? Theme { get; set; }

    public ICollection<StatusPageMonitor> Monitors { get; set; } = new List<StatusPageMonitor>();
}
