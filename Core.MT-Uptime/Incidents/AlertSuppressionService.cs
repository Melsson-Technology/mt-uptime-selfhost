using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Notifications;

namespace MT.Uptime.Core.Incidents;

/// <summary>Whether a notification should go out, and if not, why not (for the log).</summary>
public sealed record SuppressionDecision(bool Suppress, string? Reason)
{
    public static readonly SuppressionDecision Allow = new(false, null);
    public static SuppressionDecision Because(string reason) => new(true, reason);

    /// <summary>
    /// The incident this alert belongs to, if one was found while deciding. Carried on the decision so the
    /// dispatcher can enrich the alert without repeating the lookup — deciding whether to send and
    /// deciding what to say both need the same incident.
    /// </summary>
    public Incident? Incident { get; init; }
}

/// <summary>
/// The single place that answers <i>should this notification fire?</i>
/// <para>
/// Acknowledgement, snooze and maintenance windows are all different ways of asking the same question, so
/// they are answered in one service rather than three checks scattered through the dispatcher. Every rule
/// here is about <b>suppressing bad news</b>.
/// </para>
/// <para>
/// <b>A recovery is never suppressed.</b> Channels that hold state — PagerDuty most obviously — open a
/// remote incident on the alert and close it on the recovery. Dropping a recovery because a window was
/// active or someone had acknowledged the outage would strand that remote incident open forever, with no
/// path back other than a human closing it by hand in the other tool.
/// </para>
/// </summary>
public sealed class AlertSuppressionService(
    IncidentService incidents,
    Maintenance.MaintenanceWindowService maintenance)
{
    public async Task<SuppressionDecision> EvaluateAsync(NotificationEvent evt, CancellationToken ct = default)
    {
        // See the class remarks: recoveries always go out. The incident is still resolved and attached,
        // because a recovery notice benefits from the same context as the outage notice did.
        var incident = await incidents.FindOpenForMonitorAsync(evt.MonitorId, evt.At, ct);

        if (evt.Kind == NotifyKind.Up)
            return SuppressionDecision.Allow with { Incident = incident };

        if (await maintenance.ActiveForAsync(evt.MonitorId, evt.At, ct) is { } window)
            return SuppressionDecision.Because($"maintenance window '{window.Name}'") with { Incident = incident };

        if (incident is null) return SuppressionDecision.Allow;

        if (incident.AcknowledgedAt is not null)
            return SuppressionDecision.Because($"incident #{incident.Id} acknowledged") with { Incident = incident };

        if (incident.SnoozedUntil is { } until && until > evt.At)
            return SuppressionDecision.Because($"incident #{incident.Id} snoozed until {until:u}") with { Incident = incident };

        return SuppressionDecision.Allow with { Incident = incident };
    }
}
