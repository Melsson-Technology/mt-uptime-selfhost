using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Incidents;
using MT.Uptime.Core.Maintenance;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Notifications;

namespace MT.Uptime.Tests;

public class MaintenanceWindowTests
{
    private static MaintenanceWindow Weekly(DayOfWeek day, int startMinute, int durationMinutes, string tz = "UTC") =>
        new()
        {
            Name = "weekly",
            Enabled = true,
            Recurrence = MaintenanceRecurrence.Weekly,
            DaysOfWeekMask = 1 << (int)day,
            StartMinuteOfDay = startMinute,
            DurationMinutes = durationMinutes,
            TimeZoneId = tz,
            AppliesToAllMonitors = true,
        };

    // --- Schedule evaluation -----------------------------------------------------------------------

    [Fact]
    public void One_off_window_is_open_only_between_its_bounds()
    {
        var start = new DateTime(2026, 8, 16, 1, 0, 0, DateTimeKind.Utc);
        var w = new MaintenanceWindow
        {
            Name = "release",
            Enabled = true,
            Recurrence = MaintenanceRecurrence.Once,
            StartsAt = start,
            EndsAt = start.AddHours(2),
            AppliesToAllMonitors = true,
        };

        Assert.False(MaintenanceWindowService.IsOpenAt(w, start.AddMinutes(-1)));
        Assert.True(MaintenanceWindowService.IsOpenAt(w, start));
        Assert.True(MaintenanceWindowService.IsOpenAt(w, start.AddMinutes(119)));
        // End is exclusive, so two adjacent windows cannot both claim the boundary instant.
        Assert.False(MaintenanceWindowService.IsOpenAt(w, start.AddHours(2)));
    }

    [Fact]
    public void Weekly_window_opens_on_its_day_and_time()
    {
        var at = new DateTime(2026, 8, 16, 2, 30, 0, DateTimeKind.Utc);
        var w = Weekly(at.DayOfWeek, startMinute: 120, durationMinutes: 60);

        Assert.True(MaintenanceWindowService.IsOpenAt(w, at));
        Assert.False(MaintenanceWindowService.IsOpenAt(w, at.AddHours(1)));   // past 03:00
        Assert.False(MaintenanceWindowService.IsOpenAt(w, at.AddDays(1)));    // wrong day
    }

    [Fact]
    public void Weekly_window_stays_open_past_midnight()
    {
        // 23:00 for two hours: the second hour falls on the *next* calendar day, which only works
        // because the previous local day is checked as well as the current one.
        var start = new DateTime(2026, 8, 16, 23, 30, 0, DateTimeKind.Utc);
        var w = Weekly(start.DayOfWeek, startMinute: 23 * 60, durationMinutes: 120);

        Assert.True(MaintenanceWindowService.IsOpenAt(w, start));
        Assert.True(MaintenanceWindowService.IsOpenAt(w, start.AddHours(1)));   // 00:30 the next day
        Assert.False(MaintenanceWindowService.IsOpenAt(w, start.AddHours(2)));  // 01:30, closed
    }

    [Fact]
    public void Weekly_window_is_scheduled_in_its_own_zone()
    {
        // "Sundays at 02:00 New York" must mean local wall-clock. In August that zone is UTC-4, so the
        // window runs 06:00–07:00 UTC — and is firmly shut at 02:30 UTC, which is the previous evening
        // there. Evaluating in UTC instead would get both of these backwards.
        const string zone = "America/New_York";
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);

        var inside = new DateTime(2026, 8, 16, 6, 30, 0, DateTimeKind.Utc);
        var localDay = TimeZoneInfo.ConvertTimeFromUtc(inside, tz).DayOfWeek;
        var w = Weekly(localDay, startMinute: 120, durationMinutes: 60, tz: zone);

        Assert.True(MaintenanceWindowService.IsOpenAt(w, inside));
        Assert.False(MaintenanceWindowService.IsOpenAt(w, new DateTime(2026, 8, 16, 2, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Unknown_timezone_falls_back_to_utc_rather_than_throwing()
    {
        // This runs on the heartbeat path; throwing here would stop the writer for every monitor.
        var at = new DateTime(2026, 8, 16, 2, 30, 0, DateTimeKind.Utc);
        var w = Weekly(at.DayOfWeek, startMinute: 120, durationMinutes: 60, tz: "Not/ARealZone");

        Assert.True(MaintenanceWindowService.IsOpenAt(w, at));
    }

    [Fact]
    public void Disabled_window_is_never_open()
    {
        var at = new DateTime(2026, 8, 16, 2, 30, 0, DateTimeKind.Utc);
        var w = Weekly(at.DayOfWeek, startMinute: 120, durationMinutes: 60);
        w.Enabled = false;

        Assert.False(MaintenanceWindowService.IsOpenAt(w, at));
    }

    // --- Scope -------------------------------------------------------------------------------------

    [Fact]
    public async Task Tag_scope_covers_the_monitors_carrying_that_tag()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var covered = await tdb.SeedMonitorAsync("tagged");
        var other = await tdb.SeedMonitorAsync("untagged");

        int tagId;
        await using (var db = tdb.CreateDbContext())
        {
            var tag = new Tag { Name = "prod", Colour = "#ff0000" };
            db.Tags.Add(tag);
            await db.SaveChangesAsync();
            db.MonitorTags.Add(new MonitorTag { MonitorId = covered, TagId = tag.Id });
            await db.SaveChangesAsync();
            tagId = tag.Id;
        }

        var at = new DateTime(2026, 8, 16, 2, 30, 0, DateTimeKind.Utc);
        var svc = new MaintenanceWindowService(tdb);
        var window = Weekly(at.DayOfWeek, startMinute: 120, durationMinutes: 60);
        window.AppliesToAllMonitors = false;
        Assert.Null(await svc.SaveAsync(window, [], [tagId]));

        Assert.True(await svc.IsInMaintenanceAsync(covered, at));
        Assert.False(await svc.IsInMaintenanceAsync(other, at));
    }

    [Fact]
    public async Task Applies_to_all_covers_a_monitor_created_afterwards()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var at = new DateTime(2026, 8, 16, 2, 30, 0, DateTimeKind.Utc);
        var svc = new MaintenanceWindowService(tdb);
        Assert.Null(await svc.SaveAsync(Weekly(at.DayOfWeek, 120, 60), [], []));

        var later = await tdb.SeedMonitorAsync("built-later");
        Assert.True(await svc.IsInMaintenanceAsync(later, at));
    }

    [Fact]
    public async Task Save_rejects_a_window_that_covers_nothing()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var svc = new MaintenanceWindowService(tdb);
        var w = Weekly(DayOfWeek.Sunday, 120, 60);
        w.AppliesToAllMonitors = false;

        Assert.NotNull(await svc.SaveAsync(w, [], []));
    }

    // --- Suppression -------------------------------------------------------------------------------

    [Fact]
    public async Task Maintenance_suppresses_the_outage_alert_but_never_the_recovery()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync("site", MonitorType.Push);
        var at = new DateTime(2026, 8, 16, 2, 30, 0, DateTimeKind.Utc);

        var maintenance = new MaintenanceWindowService(tdb);
        Assert.Null(await maintenance.SaveAsync(Weekly(at.DayOfWeek, 120, 60), [], []));

        var incidents = new IncidentService(
            tdb,
            new CorrelationKeyResolver(NullLogger<CorrelationKeyResolver>.Instance),
            Options.Create(new EngineOptions()),
            NullLogger<IncidentService>.Instance);
        var suppression = new AlertSuppressionService(incidents, maintenance);

        var down = new NotificationEvent(id, "site", MonitorStatus.Down, MonitorStatus.Up, at, "boom", null, NotifyKind.Down);
        var up = down with { Kind = NotifyKind.Up, NewStatus = MonitorStatus.Up, OldStatus = MonitorStatus.Down };

        Assert.True((await suppression.EvaluateAsync(down)).Suppress);
        // A window entered mid-outage must still let the recovery through, or a stateful channel is left
        // holding a remote incident it can never close.
        Assert.False((await suppression.EvaluateAsync(up)).Suppress);

        // Outside the window the same alert goes out.
        var later = down with { At = at.AddHours(5) };
        Assert.False((await suppression.EvaluateAsync(later)).Suppress);
    }

    // --- Uptime maths ------------------------------------------------------------------------------

    [Fact]
    public async Task Maintenance_beats_leave_uptime_untouched_rather_than_counting_as_downtime()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync();
        var stats = new MonitorStatsService(tdb);
        var start = DateTime.UtcNow.AddMinutes(-30);

        await tdb.AddBeatsAsync(id, start, MonitorStatus.Up, 10);
        Assert.Equal(100.0, (await stats.GetUptimeAsync(id, TimeSpan.FromHours(1)))!.Value, 3);

        // Ten genuinely-down beats, all inside a window. Counted as downtime this would read 50%;
        // counted as uptime it would be a lie. Excluded, it stays 100% of what was measured.
        await tdb.AddBeatsAsync(id, start.AddMinutes(1), MonitorStatus.Down, 10, maintenance: true);
        Assert.Equal(100.0, (await stats.GetUptimeAsync(id, TimeSpan.FromHours(1)))!.Value, 3);

        // And the record itself is untouched: the beats are still Down in the history.
        await using var db = tdb.CreateDbContext();
        Assert.Equal(10, await db.Heartbeats.CountAsync(h => h.Status == MonitorStatus.Down && h.Maintenance));
    }

    [Fact]
    public async Task Rollup_counts_maintenance_outside_the_uptime_denominator()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync();
        var day = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-2), DateTimeKind.Utc);

        await tdb.AddBeatsAsync(id, day.AddHours(1), MonitorStatus.Up, 8);
        await tdb.AddBeatsAsync(id, day.AddHours(2), MonitorStatus.Down, 6, maintenance: true);

        await tdb.NewRetention(rawDays: 90).RunCleanupAsync();

        await using var db = tdb.CreateDbContext();
        var bucket = await db.StatRollups.SingleAsync(r => r.MonitorId == id && r.Period == RollupPeriod.Daily);

        Assert.Equal(8, bucket.UpCount);
        Assert.Equal(0, bucket.DownCount);        // the down beats were maintenance, so not downtime
        Assert.Equal(6, bucket.MaintenanceCount);
        // Total is the denominator already — maintenance is outside it, not subtracted from it.
        Assert.Equal(8, bucket.Total);
        Assert.Equal(8, bucket.AvailableCount);
    }
}
