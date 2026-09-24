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
            // The code from the failing check itself, alongside the plain-language reading of it.
            // Outside `diagnostics` because these are properties of the event, not of the lookup the
            // enricher performs, and they are present even when enrichment failed entirely.
            statusCode = evt.StatusCode,
            statusCodeMeaning = StatusCodeGloss.For(evt.StatusCode),
            attempt = evt.Attempt,
            // BREAKING (see CHANGELOG): `lastStatusCode` was renamed to `lastGoodStatusCode` and its
            // meaning pinned down. It used to be "the newest heartbeat's code", which raced the
            // heartbeat writer and so returned either the failure or the state before it depending on
            // timing. Consumers wanting the failing code should read `statusCode` above.
            diagnostics = evt.Enrichment is null ? null : new
            {
                resolvedAddress = evt.Enrichment.ResolvedAddress,
                lastGoodStatusCode = evt.Enrichment.LastGoodStatusCode,
                lastGoodResponseTimeMs = evt.Enrichment.LastGoodResponseTimeMs,
                lastGoodAt = evt.Enrichment.LastGoodAt?.ToString("o"),
                previousStateSince = evt.Enrichment.PreviousStateSince?.ToString("o"),
                recentResponseTimesMs = evt.Enrichment.RecentResponseTimesMs,
                certificateExpiresAt = evt.Enrichment.CertificateExpiresAt?.ToString("o"),
            },
            // What the failing check itself saw: where the time went, the allowlisted response headers
            // (cf-ray is the one a CDN's support desk asks for) and the start of the error page. The
            // text channels have always rendered this record; a webhook is declared Rich too, and used
            // to drop it, so the one consumer most able to use the evidence structured got none of it.
            // Null on a healthy check and for probes that collect none, which is everything but HTTP.
            evidence = Evidence(evt.Diagnostics),
        };

        var resp = await Http.PostAsJsonAsync(url, payload, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// The evidence as its own object, named after <see cref="Monitoring.CheckDiagnostics"/> so the
    /// payload and the heartbeat's stored copy read the same. Null when the check collected none.
    /// </summary>
    private static object? Evidence(Monitoring.CheckDiagnostics? d) => d is null ? null : new
    {
        timings = d.Timings is not { } t ? null : new
        {
            dnsMs = t.DnsMs,
            connectMs = t.ConnectMs,
            tlsMs = t.TlsMs,
            timeToFirstByteMs = t.TimeToFirstByteMs,
            totalMs = t.TotalMs,
            connectionReused = t.ConnectionReused,
        },
        headers = d.Headers,
        bodySnippet = d.BodySnippet,
        finalUrl = d.FinalUrl,
        httpVersion = d.HttpVersion,
    };
}
