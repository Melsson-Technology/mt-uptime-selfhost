using System.Net.Http.Json;
using MT.Uptime.Core.Security;

namespace MT.Uptime.Core.Notifications;

/// <summary>POSTs a JSON payload describing the alert to an arbitrary webhook URL.</summary>
public sealed class WebhookNotificationChannel(IHttpClientFactory http, ISecretProtector protector)
    : WebhookChannelBase(http, protector), INotificationChannel
{
    public NotificationChannelType Type => NotificationChannelType.Webhook;

    public async Task<bool> SendAsync(NotificationEvent evt, string configJson, CancellationToken ct)
    {
        var url = Reveal(TryDeserialize<WebhookChannelConfig>(configJson)?.Url);
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Incident and enrichment are nested rather than flattened, and stay null when absent, so an
        // existing consumer parsing the original fields is unaffected by their arrival.
        var payload = new
        {
            monitorId = evt.MonitorId,
            monitor = evt.MonitorName,
            kind = evt.Kind.ToString(),
            status = evt.NewStatus.ToString(),
            previousStatus = evt.OldStatus.ToString(),
            message = evt.Message,
            responseTimeMs = evt.ResponseTimeMs,
            timestamp = evt.At.ToString("o"),
            incident = evt.Incident is null ? null : new
            {
                id = evt.Incident.Id,
                monitorCount = evt.Incident.MonitorCount,
                correlated = evt.Incident.IsCorrelated,
                sharedInfrastructure = evt.Incident.SharedInfrastructure,
                otherAffectedMonitors = evt.Incident.OtherAffectedMonitors,
                startedAt = evt.Incident.StartedAt.ToString("o"),
                acknowledged = evt.Incident.Acknowledged,
            },
            diagnostics = evt.Enrichment is null ? null : new
            {
                resolvedAddress = evt.Enrichment.ResolvedAddress,
                lastStatusCode = evt.Enrichment.LastStatusCode,
                recentResponseTimesMs = evt.Enrichment.RecentResponseTimesMs,
                certificateExpiresAt = evt.Enrichment.CertificateExpiresAt?.ToString("o"),
            },
        };

        var resp = await Http.PostAsJsonAsync(url, payload, ct);
        return resp.IsSuccessStatusCode;
    }
}
