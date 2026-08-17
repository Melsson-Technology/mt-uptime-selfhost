using System.Net.Http.Json;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Core.Notifications;

/// <summary>
/// Raises and resolves PagerDuty incidents through the Events API v2.
/// <para>
/// Unlike every other channel here, PagerDuty is not a message bus — it holds <em>state</em>. So this
/// does not post "recovered" as another alert: a recovery sends <c>event_action: resolve</c> against the
/// same <c>dedup_key</c>, which closes the incident and stops the escalation. Sending a second trigger
/// instead would leave whoever is on call being paged about a service that came back twenty minutes ago,
/// which is how teams end up muting the integration entirely.
/// </para>
/// <para>
/// The same key makes repeat-while-down alerts free: PagerDuty deduplicates a trigger against an open
/// incident, so <c>ResendDown</c> updates rather than multiplying.
/// </para>
/// </summary>
public sealed class PagerDutyNotificationChannel(IHttpClientFactory http, ISecretProtector protector)
    : WebhookChannelBase(http, protector), INotificationChannel
{
    /// <summary>Events API v2 endpoint. Fixed, so it is not part of the channel's configuration.</summary>
    internal const string EventsUrl = "https://events.pagerduty.com/v2/enqueue";

    public NotificationChannelType Type => NotificationChannelType.PagerDuty;

    public async Task<bool> SendAsync(NotificationEvent evt, string configJson, CancellationToken ct)
    {
        var key = Reveal(TryDeserialize<PagerDutyChannelConfig>(configJson)?.RoutingKey);
        if (string.IsNullOrWhiteSpace(key)) return false;

        var resolving = NotificationRenderer.SeverityOf(evt.Kind) == AlertSeverity.Good;
        var (tag, verb) = NotificationRenderer.Describe(evt.Kind);

        object payload = resolving
            // A resolve carries no payload — PagerDuty ignores it and only needs to know which incident.
            ? new
            {
                routing_key = key,
                event_action = "resolve",
                dedup_key = DedupKey(evt.MonitorId),
            }
            : new
            {
                routing_key = key,
                event_action = "trigger",
                dedup_key = DedupKey(evt.MonitorId),
                payload = new
                {
                    // The correlation goes in the summary, which is what shows on the phone and in the
                    // incident list; the detail below is only visible once someone opens it.
                    summary = evt.Incident is { IsCorrelated: true } c
                        ? $"{tag}: {evt.MonitorName} {verb} (+{c.MonitorCount - 1} more)"
                        : $"{tag}: {evt.MonitorName} {verb}",
                    severity = SeverityOf(NotificationRenderer.SeverityOf(evt.Kind)),
                    source = evt.MonitorName,
                    timestamp = evt.At.ToString("o"),
                    custom_details = new
                    {
                        detail = evt.Message,
                        response_time_ms = evt.ResponseTimeMs,
                        incident_id = evt.Incident?.Id,
                        monitors_affected = evt.Incident?.MonitorCount,
                        shared_infrastructure = evt.Incident?.SharedInfrastructure,
                        also_affected = evt.Incident?.OtherAffectedMonitors,
                        resolved_address = evt.Enrichment?.ResolvedAddress,
                        last_status_code = evt.Enrichment?.LastStatusCode,
                        recent_response_times_ms = evt.Enrichment?.RecentResponseTimesMs,
                        certificate_expires_at = evt.Enrichment?.CertificateExpiresAt?.ToString("o"),
                    },
                },
            };

        var resp = await Http.PostAsJsonAsync(EventsUrl, payload, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Stable per monitor, so a trigger and its later resolve refer to the same incident. Anything
    /// time-varying here — a timestamp, a random id — would leave every incident permanently open.
    /// </summary>
    internal static string DedupKey(int monitorId) => $"mt-uptime-monitor-{monitorId}";

    /// <summary>PagerDuty's severity vocabulary.</summary>
    internal static string SeverityOf(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Bad => "critical",
        AlertSeverity.Warning => "warning",
        _ => "info",
    };
}
