using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests;

public class RetentionTests
{
    private static DateTime DaysAgoMidnight(int n) =>
        DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-n), DateTimeKind.Utc);

    [Fact]
    public async Task RollUp_produces_correct_daily_bucket()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync();
        var day = DaysAgoMidnight(2); // a complete past day
        await tdb.AddBeatsAsync(id, day.AddHours(1), MonitorStatus.Up, 9);
        await tdb.AddBeatsAsync(id, day.AddHours(2), MonitorStatus.Down, 1);

        await tdb.NewRetention(rawDays: 90).RunCleanupAsync();

        await using var db = tdb.CreateDbContext();
        var daily = await db.StatRollups
            .Where(r => r.MonitorId == id && r.Period == RollupPeriod.Daily)
            .ToListAsync();

        var bucket = Assert.Single(daily);
        Assert.Equal(day, bucket.BucketStart);
        Assert.Equal(9, bucket.UpCount);
        Assert.Equal(1, bucket.DownCount);
        Assert.Equal(0, bucket.PendingCount);
    }

    [Fact]
    public async Task RollUp_is_idempotent_across_runs()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync();
        await tdb.AddBeatsAsync(id, DaysAgoMidnight(2).AddHours(3), MonitorStatus.Up, 5);

        var svc = tdb.NewRetention(rawDays: 90);
        await svc.RunCleanupAsync();
        await svc.RunCleanupAsync(); // second pass must not duplicate buckets

        await using var db = tdb.CreateDbContext();
        Assert.Single(await db.StatRollups.Where(r => r.Period == RollupPeriod.Daily).ToListAsync());
    }

    [Fact]
    public async Task Prune_removes_old_raw_keeps_recent()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync();
        await tdb.AddBeatsAsync(id, DateTime.UtcNow.AddDays(-10), MonitorStatus.Up, 5);
        await tdb.AddBeatsAsync(id, DateTime.UtcNow.AddMinutes(-5), MonitorStatus.Up, 5);

        var result = await tdb.NewRetention(rawDays: 7).RunCleanupAsync();

        Assert.Equal(5, result.RawHeartbeatsPruned);
        await using var db = tdb.CreateDbContext();
        var remaining = await db.Heartbeats.Where(h => h.MonitorId == id).ToListAsync();
        Assert.Equal(5, remaining.Count);
        Assert.All(remaining, h => Assert.True(h.Timestamp >= DateTime.UtcNow.AddDays(-7)));
    }

    [Fact]
    public async Task Uptime_short_window_uses_raw()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync();
        await tdb.AddBeatsAsync(id, DateTime.UtcNow.AddMinutes(-30), MonitorStatus.Up, 8);
        await tdb.AddBeatsAsync(id, DateTime.UtcNow.AddMinutes(-20), MonitorStatus.Down, 2);

        var uptime = await new MonitorStatsService(tdb).GetUptimeAsync(id, TimeSpan.FromHours(24));

        Assert.NotNull(uptime);
        Assert.Equal(80.0, uptime!.Value, 3); // 8 up / 10 total
    }

    [Fact]
    public async Task Uptime_reads_rollups_after_raw_pruned()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync();
        await tdb.AddBeatsAsync(id, DaysAgoMidnight(3).AddHours(1), MonitorStatus.Up, 9);
        await tdb.AddBeatsAsync(id, DaysAgoMidnight(3).AddHours(2), MonitorStatus.Down, 1);
        await tdb.AddBeatsAsync(id, DaysAgoMidnight(2).AddHours(1), MonitorStatus.Up, 10);

        await tdb.NewRetention(rawDays: 1).RunCleanupAsync(); // prune all seeded raw, keep rollups

        await using (var db = tdb.CreateDbContext())
            Assert.Equal(0, await db.Heartbeats.CountAsync(h => h.MonitorId == id));

        var uptime = await new MonitorStatsService(tdb).GetUptimeAsync(id, TimeSpan.FromDays(30));

        Assert.NotNull(uptime);
        Assert.Equal(95.0, uptime!.Value, 3); // 19 up / 20 total, entirely from daily rollups
    }

    [Fact]
    public async Task Uptime_stitches_rollups_and_recent_raw()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync();
        await tdb.AddBeatsAsync(id, DaysAgoMidnight(5).AddHours(1), MonitorStatus.Up, 10); // complete day → rollup
        await tdb.AddBeatsAsync(id, DateTime.UtcNow.AddMinutes(-10), MonitorStatus.Down, 10); // today → raw

        await tdb.NewRetention(rawDays: 3).RunCleanupAsync(); // prunes the 5-day-old raw, keeps today

        await using (var db = tdb.CreateDbContext())
        {
            Assert.Equal(10, await db.Heartbeats.CountAsync(h => h.MonitorId == id));
            Assert.True(await db.StatRollups.AnyAsync(r => r.MonitorId == id && r.Period == RollupPeriod.Daily));
        }

        var uptime = await new MonitorStatsService(tdb).GetUptimeAsync(id, TimeSpan.FromDays(30));

        Assert.NotNull(uptime);
        Assert.Equal(50.0, uptime!.Value, 3); // 10 up (rollup) / 20 total (10 rollup up + 10 raw down)
    }

    // --- the per-process switch ------------------------------------------------------------------

    [Fact]
    public async Task The_daily_timer_does_not_start_when_retention_is_disabled_for_this_process()
    {
        // Retention writes raw SQL, and raw SQL sees the whole table. A deployment where several
        // processes share one database therefore has to run it centrally or not at all, which is what
        // this switch is for — see EngineOptions.RunRetention.
        await using var tdb = await TestDatabase.CreateAsync();

        // Await completion rather than inspecting it. On .NET 10 `BackgroundService.ExecuteTask` reads
        // `WaitingForActivation` immediately after `StartAsync` even for a service whose `ExecuteAsync`
        // returns `Task.CompletedTask`, so `IsCompleted` there is not a signal — it is false either way.
        var off = tdb.NewRetention(rawDays: 7, runRetention: false);
        await off.StartAsync(CancellationToken.None);

        var finished = await Task.WhenAny(off.ExecuteTask!, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(off.ExecuteTask, finished);   // returned, instead of waiting out the startup delay
        await off.StopAsync(CancellationToken.None);

        // Enabled, the same service sits in a thirty-second startup delay, so a moment later it is
        // still running. That is the contrast the switch is supposed to make.
        var on = tdb.NewRetention(rawDays: 7);
        await on.StartAsync(CancellationToken.None);
        await Task.Delay(250);
        Assert.False(on.ExecuteTask!.IsCompleted);
        await on.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disabling_the_timer_leaves_the_manual_run_working()
    {
        // The switch turns off the schedule, not the capability. Whatever is allowed to see the whole
        // database still has to be able to call this — and so does the Settings page button.
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync();
        await tdb.AddBeatsAsync(id, DateTime.UtcNow.AddDays(-10), MonitorStatus.Up, 5);

        var result = await tdb.NewRetention(rawDays: 7, runRetention: false).RunCleanupAsync();

        Assert.Equal(5, result.RawHeartbeatsPruned);
    }
}
