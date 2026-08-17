namespace MT.Uptime.Core.Domain;

/// <summary>Join entity: the ordered set of monitors shown on a public status page.</summary>
public class StatusPageMonitor
{
    public int StatusPageId { get; set; }
    public StatusPage? StatusPage { get; set; }

    public int MonitorId { get; set; }
    public Monitor? Monitor { get; set; }

    public int SortOrder { get; set; }
}
