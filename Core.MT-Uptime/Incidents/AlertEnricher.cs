using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Notifications;

namespace MT.Uptime.Core.Incidents;

/// <summary>
/// Attaches incident and diagnostic context to an alert just before it is delivered.
/// <para>
/// Runs at dispatch rather than at check time on purpose. The check path is the hot path and must stay
/// cheap; an alert is a rare event that can afford a couple of reads. It also has to run here because the
/// incident does not exist yet when the check decides to alert — the writer attaches it concurrently.
/// </para>
/// <para>
/// <b>Enrichment never fails an alert.</b> Every field is optional and every failure is swallowed: a
/// notification that says less is far better than one that does not arrive.
/// </para>
/// </summary>
public sealed class AlertEnricher(
    IDbContextFactory<AppDbContext> factory,
    CorrelationKeyResolver keys,
    ILogger<AlertEnricher> log,
    IOptions<EngineOptions>? options = null)
{
    /// <summary>
    /// Builds the "see everything" link for an alert, or null when no public origin is configured.
    /// Points at the incident when there is one — that page shows every monitor involved — and at the
    /// monitor otherwise.
    /// </summary>
    private string? DetailsUrl(int monitorId, Incident? incident)
    {
        var origin = options?.Value.PublicBaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(origin)) return null;

        return incident is not null
            ? $"{origin}/incidents/{incident.Id}"
            : $"{origin}/monitors/{monitorId}";
    }

    /// <summary>How many recent response times to include, newest last.</summary>
    private const int RecentSamples = 5;

    /// <summary>Returns the event with incident and diagnostic context attached where available.</summary>
    public async Task<NotificationEvent> EnrichAsync(
        NotificationEvent evt, Incident? incident, CancellationToken ct = default)
    {
        try
        {
            return evt with
            {
                Incident = Summarize(incident, evt.MonitorId),
                Enrichment = await GatherAsync(evt, ct),
                DetailsUrl = DetailsUrl(evt.MonitorId, incident),
            };
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not enrich the alert for '{Monitor}'; sending it plain", evt.MonitorName);
            return evt;
        }
    }

    /// <summary>
    /// Reduces the incident to what the alert should say. The monitor being alerted on is excluded from
    /// the "also affected" list — the reader already knows about that one, it is the subject of the alert.
    /// </summary>
    private static IncidentSummary? Summarize(Incident? incident, int alertingMonitorId)
    {
        if (incident is null) return null;

        var others = incident.Events
            .Where(e => e.MonitorId != alertingMonitorId)
            .Select(e => e.Monitor?.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct()
            .ToList();

        // The alerting monitor is usually not a member yet: the writer attaches it concurrently with this
        // alert being built, and the incident was found by correlation key rather than membership. Its
        // stored count is therefore one short, which would report "2 monitors are affected" directly above
        // a list naming two *others*. Count it in ourselves rather than reading a number we know is stale.
        var isMember = incident.Events.Any(e => e.MonitorId == alertingMonitorId);
        var monitorCount = isMember ? incident.MonitorCount : incident.MonitorCount + 1;

        return new IncidentSummary(
            incident.Id,
            monitorCount,
            StripKeyPrefix(incident.CorrelationKey),
            others,
            incident.StartedAt,
            incident.AcknowledgedAt is not null);
    }

    /// <summary>Turns the internal <c>ip:</c> / <c>host:</c> key into something worth showing a human.</summary>
    private static string? StripKeyPrefix(string? key)
    {
        if (key is null) return null;
        var colon = key.IndexOf(':');
        return colon < 0 ? key : key[(colon + 1)..];
    }

    private async Task<AlertEnrichment> GatherAsync(NotificationEvent evt, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var monitor = await db.Monitors.AsNoTracking().FirstOrDefaultAsync(m => m.Id == evt.MonitorId, ct);

        // Everything below is bounded to strictly before the alert. The heartbeat writer flushes
        // asynchronously, so the beat that caused this alert may or may not be in the table yet; the
        // runner stamps both it and the event from the same `now`, which makes this comparison the one
        // thing that reads the same either way. Without it, every field here is a coin toss against the
        // writer — and that is exactly the bug that put "Last response code: 200" under a 521.
        var before = db.Heartbeats.AsNoTracking()
            .Where(h => h.MonitorId == evt.MonitorId && h.Timestamp < evt.At);

        var recent = await before
            .OrderByDescending(h => h.Timestamp)
            .Take(RecentSamples)
            .Select(h => h.ResponseTimeMs)
            .ToListAsync(ct);

        var timings = recent
            .Where(ms => ms is not null)
            .Select(ms => ms!.Value)
            .Reverse()          // oldest first, so the trend reads left to right
            .ToList();

        // The last *good* response, not the last response. See AlertEnrichment.LastGoodStatusCode.
        var lastGood = await before
            .Where(h => h.Status == MonitorStatus.Up)
            .OrderByDescending(h => h.Timestamp)
            .Select(h => new { h.StatusCode, h.ResponseTimeMs, h.Timestamp })
            .FirstOrDefaultAsync(ct);

        // A heartbeat is flagged Important exactly when it marks a state transition, so the newest one
        // before this alert is when the state we are leaving began.
        var previousStateSince = await before
            .Where(h => h.Important)
            .OrderByDescending(h => h.Timestamp)
            .Select(h => (DateTime?)h.Timestamp)
            .FirstOrDefaultAsync(ct);

        var address = monitor is null
            ? null
            : StripKeyPrefix(await keys.GetKeyAsync(monitor.Type, monitor.ConfigJson, ct));

        var cert = AlertEnrichment.IsWorthMentioning(monitor?.CertExpiresAt, evt.At)
            ? monitor!.CertExpiresAt
            : null;

        return new AlertEnrichment(
            address,
            lastGood?.StatusCode,
            lastGood?.ResponseTimeMs,
            lastGood?.Timestamp,
            timings,
            cert,
            previousStateSince);
    }
}
