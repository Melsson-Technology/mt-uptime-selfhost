using MT.Uptime.Core.Incidents;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Core.Notifications;

/// <summary>A monitor state transition worth notifying about.</summary>
public sealed record NotificationEvent(
    int MonitorId,
    string MonitorName,
    MonitorStatus NewStatus,
    MonitorStatus OldStatus,
    DateTime At,
    string? Message,
    double? ResponseTimeMs,
    NotifyKind Kind)
{
    /// <summary>
    /// The incident this alert is part of, attached by <c>AlertEnricher</c> at dispatch.
    /// <para>
    /// Not a constructor parameter because the check path that raises the event cannot know it: the
    /// incident is created by the heartbeat writer concurrently. Null means "no incident context" and
    /// every consumer must render fine without it.
    /// </para>
    /// </summary>
    public IncidentSummary? Incident { get; init; }

    /// <summary>Diagnostic context attached at dispatch. Null or partially empty is normal.</summary>
    public AlertEnrichment? Enrichment { get; init; }
}
