using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Data;

namespace MT.Uptime.Core.Monitoring;

/// <summary>Read-side queries for the monitor detail page: heartbeat history, uptime %, and events.</summary>
public sealed class MonitorStatsService(IDbContextFactory<AppDbContext> factory)
{
    /// <summary>The most recent <paramref name="take"/> heartbeats, returned in chronological order.</summary>
    public async Task<List<Heartbeat>> GetRecentHeartbeatsAsync(int monitorId, int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var list = await db.Heartbeats.AsNoTracking()
            .Where(h => h.MonitorId == monitorId)
            .OrderByDescending(h => h.Timestamp)
            .Take(take)
            .ToListAsync(ct);
        list.Reverse();
        return list;
    }

    /// <summary>Windows at or below this use raw heartbeats directly (always retained this far back).</summary>
    private static readonly TimeSpan RawWindowMax = TimeSpan.FromDays(2);

    /// <summary>
    /// Uptime percentage over the window (Up beats / total beats), or null if there are no beats.
    /// Short windows read raw heartbeats. Long windows read raw when it still covers the window
    /// (the default, most accurate); once raw has been pruned back past the window, they stitch
    /// daily <see cref="StatRollup"/> buckets (older, complete days) to the surviving raw.
    /// </summary>
    public async Task<double?> GetUptimeAsync(int monitorId, TimeSpan window, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var since = now - window;
        await using var db = await factory.CreateDbContextAsync(ct);

        if (window <= RawWindowMax)
            return await RawUptimeAsync(db, monitorId, since, ct);

        var oldestRaw = await db.Heartbeats.AsNoTracking()
            .Where(h => h.MonitorId == monitorId)
            .MinAsync(h => (DateTime?)h.Timestamp, ct);

        if (oldestRaw is null)
            return await RollupUptimeAsync(db, monitorId, since, ct);
        if (oldestRaw.Value <= since)
            return await RawUptimeAsync(db, monitorId, since, ct);
        return await StitchedUptimeAsync(db, monitorId, since, oldestRaw.Value, ct);
    }

    /// <summary>
    /// Statuses that count as available for uptime %. Degraded is included deliberately: the target
    /// answered correctly, it was just slow, so counting it as downtime would understate availability
    /// and make the slow-threshold feature punish the very monitors that enable it.
    /// </summary>
    private static bool IsAvailable(MonitorStatus s) => s is MonitorStatus.Up or MonitorStatus.Degraded;

    /// <summary>
    /// Uptime from raw heartbeats at or after <paramref name="since"/>.
    /// <para>
    /// Maintenance beats are dropped before the grouping, so they leave both the numerator and the
    /// denominator — planned work is excluded from availability rather than counted as either up or down.
    /// This mirrors <see cref="StatRollup.MaintenanceCount"/> sitting outside <see cref="StatRollup.Total"/>,
    /// which is what keeps the raw and rolled-up paths agreeing across the pruning seam.
    /// </para>
    /// </summary>
    private static async Task<double?> RawUptimeAsync(AppDbContext db, int monitorId, DateTime since, CancellationToken ct)
    {
        var counts = await db.Heartbeats.AsNoTracking()
            .Where(h => h.MonitorId == monitorId && h.Timestamp >= since && !h.Maintenance)
            .GroupBy(h => h.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        long total = counts.Sum(c => (long)c.Count);
        if (total == 0) return null;
        long up = counts.Where(c => IsAvailable(c.Status)).Sum(c => (long)c.Count);
        return (double)up / total * 100.0;
    }

    /// <summary>Uptime purely from daily rollup buckets (used when no raw survives at all).</summary>
    private static async Task<double?> RollupUptimeAsync(AppDbContext db, int monitorId, DateTime since, CancellationToken ct)
    {
        var sinceDay = DateTime.SpecifyKind(since.Date, DateTimeKind.Utc);
        var roll = await DailyRollupTotalsAsync(db, monitorId, sinceDay, upperExclusive: null, ct);
        return roll.Total == 0 ? null : (double)roll.Up / roll.Total * 100.0;
    }

    /// <summary>
    /// Uptime stitched from daily rollups for whole days before raw coverage begins, plus the
    /// surviving raw for everything from that day onward. The seam is a day boundary, so the two
    /// sources never overlap.
    /// </summary>
    private static async Task<double?> StitchedUptimeAsync(
        AppDbContext db, int monitorId, DateTime since, DateTime oldestRaw, CancellationToken ct)
    {
        var sinceDay = DateTime.SpecifyKind(since.Date, DateTimeKind.Utc);
        var seamDay = DateTime.SpecifyKind(oldestRaw.Date, DateTimeKind.Utc);

        var roll = await DailyRollupTotalsAsync(db, monitorId, sinceDay, upperExclusive: seamDay, ct);

        var rawCounts = await db.Heartbeats.AsNoTracking()
            .Where(h => h.MonitorId == monitorId && h.Timestamp >= seamDay && !h.Maintenance)
            .GroupBy(h => h.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        long up = roll.Up + rawCounts.Where(c => IsAvailable(c.Status)).Sum(c => (long)c.Count);
        long total = roll.Total + rawCounts.Sum(c => (long)c.Count);
        return total == 0 ? null : (double)up / total * 100.0;
    }

    private static async Task<(long Up, long Total)> DailyRollupTotalsAsync(
        AppDbContext db, int monitorId, DateTime fromInclusive, DateTime? upperExclusive, CancellationToken ct)
    {
        var q = db.StatRollups.AsNoTracking()
            .Where(r => r.MonitorId == monitorId && r.Period == RollupPeriod.Daily && r.BucketStart >= fromInclusive);
        if (upperExclusive is { } upper)
            q = q.Where(r => r.BucketStart < upper);

        var agg = await q
            .GroupBy(_ => 1)
            .Select(g => new
            {
                // Degraded buckets count as available, matching IsAvailable on the raw path.
                Up = (long)g.Sum(x => x.UpCount + x.DegradedCount),
                Total = (long)g.Sum(x => x.UpCount + x.DownCount + x.PendingCount + x.DegradedCount),
            })
            .FirstOrDefaultAsync(ct);

        return (agg?.Up ?? 0, agg?.Total ?? 0);
    }

    public async Task<List<MonitorEvent>> GetEventsAsync(int monitorId, int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MonitorEvents.AsNoTracking()
            .Where(e => e.MonitorId == monitorId)
            .OrderByDescending(e => e.StartedAt)
            .Take(take)
            .ToListAsync(ct);
    }
}
