using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Incidents;
using MT.Uptime.Core.Maintenance;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests;

public class StatusPageIncidentTests
{
    private static IncidentService Incidents(TestDatabase tdb) =>
        new(tdb,
            new CorrelationKeyResolver(NullLogger<CorrelationKeyResolver>.Instance),
            Options.Create(new EngineOptions()));

    private static async Task<int> SeedStatusPageAsync(TestDatabase tdb, string slug, params int[] monitorIds)
    {
        await using var db = tdb.CreateDbContext();
        var page = new StatusPage { Slug = slug, Title = slug, Published = true };
        db.StatusPages.Add(page);
        await db.SaveChangesAsync();

        for (var i = 0; i < monitorIds.Length; i++)
            db.StatusPageMonitors.Add(new StatusPageMonitor { StatusPageId = page.Id, MonitorId = monitorIds[i], SortOrder = i });
        await db.SaveChangesAsync();
        return page.Id;
    }

    private static async Task<long> SeedIncidentAsync(
        TestDatabase tdb, DateTime startedAt, DateTime? resolvedAt, bool published, params int[] monitorIds)
    {
        await using var db = tdb.CreateDbContext();
        var incident = new Incident
        {
            Title = "first-to-fail",
            StartedAt = startedAt,
            LastEventAt = startedAt,
            ResolvedAt = resolvedAt,
            Severity = MonitorStatus.Down,
            MonitorCount = monitorIds.Length,
            Published = published,
        };

        foreach (var id in monitorIds)
            incident.Events.Add(new MonitorEvent
            {
                MonitorId = id,
                StartedAt = startedAt,
                ResolvedAt = resolvedAt,
                FromStatus = MonitorStatus.Up,
                ToStatus = MonitorStatus.Down,
            });

        db.Incidents.Add(incident);
        await db.SaveChangesAsync();
        return incident.Id;
    }

    [Fact]
    public async Task A_status_page_never_names_a_monitor_it_does_not_list()
    {
        // The leak this projection exists to prevent: correlation groups monitors by shared host, which
        // routinely means two customers on one box. Customer A's status page must not learn that
        // customer B exists — including via the incident's own title, which names whoever failed first.
        await using var tdb = await TestDatabase.CreateAsync();
        var mine = await tdb.SeedMonitorAsync("acme-web");
        var theirs = await tdb.SeedMonitorAsync("competitor-web");
        var pageId = await SeedStatusPageAsync(tdb, "acme", mine);

        var now = DateTime.UtcNow;
        await SeedIncidentAsync(tdb, now.AddMinutes(-10), null, published: true, theirs, mine);

        var shown = await Incidents(tdb).GetForStatusPageAsync(pageId, now, TimeSpan.FromDays(7));

        var incident = Assert.Single(shown);
        Assert.Equal(["acme-web"], incident.AffectedMonitors);
        Assert.DoesNotContain("competitor-web", incident.Headline);
        Assert.Equal("acme-web", incident.Headline);
    }

    [Fact]
    public async Task Unpublished_incidents_are_withheld()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var m = await tdb.SeedMonitorAsync("web");
        var pageId = await SeedStatusPageAsync(tdb, "acme", m);

        var now = DateTime.UtcNow;
        await SeedIncidentAsync(tdb, now.AddMinutes(-10), null, published: false, m);

        Assert.Empty(await Incidents(tdb).GetForStatusPageAsync(pageId, now, TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task Resolved_incidents_linger_then_drop_off()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var m = await tdb.SeedMonitorAsync("web");
        var pageId = await SeedStatusPageAsync(tdb, "acme", m);
        var svc = Incidents(tdb);
        var now = DateTime.UtcNow;

        await SeedIncidentAsync(tdb, now.AddDays(-3), now.AddDays(-3).AddHours(1), published: true, m);

        // Still worth showing: a reader arriving days later should see what happened.
        Assert.Single(await svc.GetForStatusPageAsync(pageId, now, TimeSpan.FromDays(7)));
        Assert.Empty(await svc.GetForStatusPageAsync(pageId, now, TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task An_incident_elsewhere_does_not_appear()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var mine = await tdb.SeedMonitorAsync("acme-web");
        var theirs = await tdb.SeedMonitorAsync("other-web");
        var pageId = await SeedStatusPageAsync(tdb, "acme", mine);

        var now = DateTime.UtcNow;
        await SeedIncidentAsync(tdb, now.AddMinutes(-5), null, published: true, theirs);

        Assert.Empty(await Incidents(tdb).GetForStatusPageAsync(pageId, now, TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task Updates_are_published_newest_first()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var m = await tdb.SeedMonitorAsync("web");
        var pageId = await SeedStatusPageAsync(tdb, "acme", m);
        var svc = Incidents(tdb);

        var now = DateTime.UtcNow;
        var id = await SeedIncidentAsync(tdb, now.AddMinutes(-30), null, published: true, m);

        Assert.True(await svc.AddUpdateAsync(id, IncidentUpdateKind.Investigating, "Looking into it.", null, now.AddMinutes(-25)));
        Assert.True(await svc.AddUpdateAsync(id, IncidentUpdateKind.Identified, "Bad deploy.", null, now.AddMinutes(-10)));
        // An empty note is refused rather than published as a blank entry.
        Assert.False(await svc.AddUpdateAsync(id, IncidentUpdateKind.Monitoring, "   ", null, now));

        var incident = Assert.Single(await svc.GetForStatusPageAsync(pageId, now, TimeSpan.FromDays(7)));
        Assert.Equal(2, incident.Updates.Count);
        Assert.Equal(IncidentUpdateKind.Identified, incident.Updates[0].Kind);
        Assert.Equal("Bad deploy.", incident.Updates[0].Body);
    }

    [Fact]
    public async Task Hiding_an_incident_takes_it_off_the_page()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var m = await tdb.SeedMonitorAsync("web");
        var pageId = await SeedStatusPageAsync(tdb, "acme", m);
        var svc = Incidents(tdb);
        var now = DateTime.UtcNow;

        var id = await SeedIncidentAsync(tdb, now.AddMinutes(-10), null, published: true, m);
        Assert.Single(await svc.GetForStatusPageAsync(pageId, now, TimeSpan.FromDays(7)));

        Assert.True(await svc.SetPublishedAsync(id, false));
        Assert.Empty(await svc.GetForStatusPageAsync(pageId, now, TimeSpan.FromDays(7)));
    }

    // --- Announced maintenance ---------------------------------------------------------------------

    [Fact]
    public async Task Upcoming_maintenance_is_announced_only_when_published()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var m = await tdb.SeedMonitorAsync("web");
        var svc = new MaintenanceWindowService(tdb);
        var now = DateTime.UtcNow;

        var window = new MaintenanceWindow
        {
            Name = "Database upgrade",
            Enabled = true,
            Publish = true,
            Recurrence = MaintenanceRecurrence.Once,
            StartsAt = now.AddDays(2),
            EndsAt = now.AddDays(2).AddHours(1),
            AppliesToAllMonitors = true,
        };
        Assert.Null(await svc.SaveAsync(window, [], []));

        var announced = await svc.UpcomingForAsync([m], now, TimeSpan.FromDays(14));
        Assert.Equal("Database upgrade", Assert.Single(announced).Window.Name);
        Assert.False(announced[0].InProgress);

        // Beyond the horizon it is not yet news.
        Assert.Empty(await svc.UpcomingForAsync([m], now, TimeSpan.FromDays(1)));

        // Unpublished windows still suppress alerts but are not announced.
        var saved = (await svc.ListAsync()).Single();
        saved.Publish = false;
        Assert.Null(await svc.SaveAsync(saved, [], []));
        Assert.Empty(await svc.UpcomingForAsync([m], now, TimeSpan.FromDays(14)));
    }

    [Fact]
    public void Next_weekly_occurrence_is_found_within_the_horizon()
    {
        var w = new MaintenanceWindow
        {
            Name = "weekly",
            Enabled = true,
            Publish = true,
            Recurrence = MaintenanceRecurrence.Weekly,
            DaysOfWeekMask = 1 << (int)DayOfWeek.Sunday,
            StartMinuteOfDay = 120,
            DurationMinutes = 60,
            TimeZoneId = "UTC",
            AppliesToAllMonitors = true,
        };

        // A Monday, so the next Sunday 02:00 UTC is six days out.
        var monday = new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);

        var next = MaintenanceWindowService.CurrentOrNextOccurrence(w, monday, TimeSpan.FromDays(14));
        Assert.NotNull(next);
        Assert.Equal(new DateTime(2026, 8, 23, 2, 0, 0, DateTimeKind.Utc), next!.Value.Start);
        Assert.Equal(new DateTime(2026, 8, 23, 3, 0, 0, DateTimeKind.Utc), next.Value.End);

        // A horizon that stops short of it finds nothing.
        Assert.Null(MaintenanceWindowService.CurrentOrNextOccurrence(w, monday, TimeSpan.FromDays(2)));
    }

    [Fact]
    public void An_occurrence_in_progress_is_reported_as_current()
    {
        var w = new MaintenanceWindow
        {
            Name = "weekly",
            Enabled = true,
            Recurrence = MaintenanceRecurrence.Weekly,
            DaysOfWeekMask = 1 << (int)DayOfWeek.Sunday,
            StartMinuteOfDay = 120,
            DurationMinutes = 60,
            TimeZoneId = "UTC",
        };

        var during = new DateTime(2026, 8, 23, 2, 30, 0, DateTimeKind.Utc);
        Assert.Equal(DayOfWeek.Sunday, during.DayOfWeek);

        var now = MaintenanceWindowService.CurrentOrNextOccurrence(w, during, TimeSpan.FromDays(14));
        Assert.NotNull(now);
        Assert.Equal(new DateTime(2026, 8, 23, 2, 0, 0, DateTimeKind.Utc), now!.Value.Start);
        Assert.True(now.Value.Start <= during && during < now.Value.End);
    }
}
