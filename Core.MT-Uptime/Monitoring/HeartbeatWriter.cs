using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Incidents;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// The single writer for the high-frequency heartbeat stream. Runners enqueue <see cref="CheckOutcome"/>s;
/// this drains them one at a time and applies each (heartbeat insert + denormalized monitor update +
/// event open/resolve + incident grouping), so concurrent checks never contend on SQLite's single write lock.
/// </summary>
public sealed class HeartbeatWriter(
    IDbContextFactory<AppDbContext> factory,
    IncidentService incidents,
    MT.Uptime.Core.Maintenance.MaintenanceWindowService maintenance,
    ILogger<HeartbeatWriter> log)
    : BackgroundService
{
    private readonly Channel<CheckOutcome> _channel =
        Channel.CreateUnbounded<CheckOutcome>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(CheckOutcome outcome) => _channel.Writer.TryWrite(outcome);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var outcome in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await WriteAsync(outcome, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to persist heartbeat for monitor {MonitorId}", outcome.MonitorId);
            }
        }
    }

    private async Task WriteAsync(CheckOutcome o, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Answered from a cached snapshot, so this is an in-memory check despite running per beat.
        // The status recorded below is unaffected — the flag only removes the beat from uptime.
        var inMaintenance = await maintenance.IsInMaintenanceAsync(o.MonitorId, o.Timestamp, ct);

        db.Heartbeats.Add(new Heartbeat
        {
            MonitorId = o.MonitorId,
            Timestamp = o.Timestamp,
            Status = o.HeartbeatStatus,
            ResponseTimeMs = o.ResponseTimeMs,
            StatusCode = o.StatusCode,
            Message = o.Message,
            Important = o.Important,
            Attempt = o.Attempt,
            Maintenance = inMaintenance,
        });

        // ResolveAndOpen closes the running event first (Degraded -> Down and back), so the "resolve
        // the latest open event" lookup below never has two candidates for the same monitor.
        MonitorEvent? resolved = null;
        if (o.EventAction is EventAction.Resolve or EventAction.ResolveAndOpen)
            resolved = await ResolveOpenEventAsync(db, o, ct);

        if (o.EventAction is EventAction.Open or EventAction.ResolveAndOpen)
        {
            var opened = new MonitorEvent
            {
                MonitorId = o.MonitorId,
                FromStatus = o.FromStatus,
                ToStatus = o.ToStatus,
                StartedAt = o.Timestamp,
                Reason = o.Message,
            };
            db.MonitorEvents.Add(opened);

            // Read the monitor for its type and config — the incident grouping is inferred from what the
            // monitor points at. Only on a transition, so this is not on the per-check path.
            var monitor = await db.Monitors.AsNoTracking().FirstOrDefaultAsync(m => m.Id == o.MonitorId, ct);
            if (monitor is not null)
                await incidents.AttachAsync(db, opened, monitor, o.Timestamp, ct);
        }

        // Strictly after the attach above: an escalation resolves one event and opens another in the same
        // beat, and judged in between, every member would momentarily look resolved — splitting one
        // continuous outage into two incidents. See IncidentService.CloseIfAllResolved.
        if (resolved?.IncidentId is { } incidentId)
        {
            var incident = await incidents.LoadAsync(db, incidentId, ct);
            if (incident is not null)
                IncidentService.CloseIfAllResolved(incident, o.Timestamp);
        }

        await db.SaveChangesAsync(ct);

        // Denormalized live-status cache on the monitor row (fast dashboard load, survives restarts).
        await db.Monitors.Where(m => m.Id == o.MonitorId).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.CurrentStatus, o.HeartbeatStatus)
            .SetProperty(m => m.LastHeartbeatAt, o.Timestamp)
            .SetProperty(m => m.LastResponseTimeMs, o.ResponseTimeMs), ct);

        if (o.CertExpiresAt is not null)
        {
            await db.Monitors.Where(m => m.Id == o.MonitorId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.CertExpiresAt, o.CertExpiresAt), ct);
        }

    }

    /// <summary>
    /// Mark the monitor's most recent still-open event as resolved, stamping its duration, and return it
    /// so the caller can decide whether its incident is now fully resolved. Deliberately does not save —
    /// the caller's single <c>SaveChangesAsync</c> commits the resolve, the heartbeat, any newly-opened
    /// event and the incident change together, so a ResolveAndOpen can never half-apply.
    /// </summary>
    private static async Task<MonitorEvent?> ResolveOpenEventAsync(AppDbContext db, CheckOutcome o, CancellationToken ct)
    {
        var ev = await db.MonitorEvents
            .Where(e => e.MonitorId == o.MonitorId && e.ResolvedAt == null)
            .OrderByDescending(e => e.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (ev is null) return null;

        ev.ResolvedAt = o.Timestamp;
        ev.DurationSeconds = (long)Math.Max(0, (o.Timestamp - ev.StartedAt).TotalSeconds);
        return ev;
    }
}
