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

    /// <summary>
    /// The protocol code from the check that <em>caused</em> this alert — the 521, not whatever the
    /// heartbeat table happens to hold by the time the alert is enriched.
    /// <para>
    /// An init property rather than a constructor parameter to match <see cref="Incident"/> and
    /// <see cref="Enrichment"/> above, and because an event without one is legitimate: a push monitor
    /// going stale, or a timeout that never got far enough to receive a code.
    /// </para>
    /// <para>
    /// Distinct from <see cref="AlertEnrichment.LastGoodStatusCode"/> on purpose. This is the failure;
    /// that is the state before it. Conflating them is what produced an alert reading
    /// "Detail: Unexpected status 521" directly above "Last response code: 200".
    /// </para>
    /// </summary>
    public string? StatusCode { get; init; }

    /// <summary>
    /// Which attempt in the current failure streak this is (0 = the first failure). Straight from
    /// <c>MonitorStateMachine</c>, which already tracks it for the retry cushion.
    /// </summary>
    public int Attempt { get; init; }

    /// <summary>
    /// Evidence captured by the checker at the moment of failure — response headers, a body snippet,
    /// and the timing breakdown. Carried on the event rather than read back from the heartbeat row for
    /// the same reason <see cref="StatusCode"/> is: the row may not be flushed yet, and an alert that
    /// races its own storage is how the "Last response code" contradiction happened.
    /// </summary>
    public Monitoring.CheckDiagnostics? Diagnostics { get; init; }

    /// <summary>
    /// Where a reader can see everything this alert had to leave out — the incident page when there is
    /// an incident, the monitor's page otherwise. Null when the instance has no configured public
    /// origin, in which case alerts simply carry no link rather than a guessed one.
    /// </summary>
    public string? DetailsUrl { get; init; }
}
