using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Incidents;
using MT.Uptime.Core.Maintenance;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.StatusPages;

namespace MT.Uptime.Tests;

public class StatusPageIncidentTests
{
    private static IncidentService Incidents(TestDatabase tdb) =>
        new(tdb,
            new CorrelationKeyResolver(NullLogger<CorrelationKeyResolver>.Instance),
            Options.Create(new EngineOptions()),
            NullLogger<IncidentService>.Instance);

    private static Task<int> SeedStatusPageAsync(TestDatabase tdb, string slug, params int[] monitorIds)
        => SeedStatusPageAsync(tdb, slug, published: true, monitorIds);

    /// <summary>
    /// Overloaded rather than given an optional parameter: C# cannot skip a positional optional argument
    /// before a <c>params</c> array, so adding <c>bool published = true</c> to the signature above would
    /// bind every existing <c>SeedStatusPageAsync(tdb, "acme", monitorId)</c> call's third argument to it
    /// and fail to compile.
    /// </summary>
    private static async Task<int> SeedStatusPageAsync(
        TestDatabase tdb, string slug, bool published, int[] monitorIds)
    {
        await using var db = tdb.CreateDbContext();
        var page = new StatusPage { Slug = slug, Title = slug, Published = published };
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
    public async Task An_unpublished_status_page_is_not_served_by_slug()
    {
        // The whole public/private boundary for status pages is one conjunct — `&& sp.Published` in
        // GetPublishedBySlugAsync — and nothing asserted it at any level. Not exploitable as shipped;
        // this exists so a refactor that lifts the filter out cannot pass unnoticed.
        await using var tdb = await TestDatabase.CreateAsync();
        var monitorId = await tdb.SeedMonitorAsync("acme-web");
        await SeedStatusPageAsync(tdb, "draft", published: false, [monitorId]);

        var service = new StatusPageService(tdb);

        Assert.Null(await service.GetPublishedBySlugAsync("draft"));
    }

    [Fact]
    public async Task A_published_status_page_is_served_by_slug()
    {
        // The positive control. Without it the test above passes just as well if the lookup broke for
        // every page, which would take every customer's status page offline and look like security.
        await using var tdb = await TestDatabase.CreateAsync();
        var monitorId = await tdb.SeedMonitorAsync("acme-web");
        await SeedStatusPageAsync(tdb, "live", published: true, [monitorId]);

        var service = new StatusPageService(tdb);
        var page = await service.GetPublishedBySlugAsync("live");

        Assert.NotNull(page);
        Assert.Equal("live", page!.Slug);
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
    public async Task A_status_page_reports_its_own_outage_window_not_the_incidents()
    {
        // Acme was down for five minutes; the incident stayed open for three hours because the other
        // customer on the same host never recovered. Publishing the incident's own timeline made Acme's
        // page read "started 01:00 · ongoing" above a monitor row saying Operational — overstating their
        // outage, contradicting itself, and leaking how long somebody else's outage ran.
        await using var tdb = await TestDatabase.CreateAsync();
        var mine = await tdb.SeedMonitorAsync("acme-web");
        var theirs = await tdb.SeedMonitorAsync("competitor-web");
        var pageId = await SeedStatusPageAsync(tdb, "acme", mine);

        var now = DateTime.UtcNow;
        var start = now.AddHours(-3);
        await using (var db = tdb.CreateDbContext())
        {
            var incident = new Incident
            {
                Title = "competitor-web",
                StartedAt = start,
                LastEventAt = start,
                ResolvedAt = null,                 // still open: their monitor never came back
                Severity = MonitorStatus.Down,
                MonitorCount = 2,
                Published = true,
            };
            incident.Events.Add(new MonitorEvent
            {
                MonitorId = theirs, StartedAt = start, ResolvedAt = null,
                FromStatus = MonitorStatus.Up, ToStatus = MonitorStatus.Down,
            });
            incident.Events.Add(new MonitorEvent
            {
                MonitorId = mine, StartedAt = start.AddMinutes(5), ResolvedAt = start.AddMinutes(10),
                FromStatus = MonitorStatus.Up, ToStatus = MonitorStatus.Degraded,
            });
            db.Incidents.Add(incident);
            await db.SaveChangesAsync();
        }

        var shown = Assert.Single(await Incidents(tdb).GetForStatusPageAsync(pageId, now, TimeSpan.FromDays(7)));

        Assert.Equal(start.AddMinutes(5), shown.StartedAt);        // ours started later
        Assert.Equal(start.AddMinutes(10), shown.ResolvedAt);      // and ended, though the incident has not
        Assert.False(shown.IsOpen);
        Assert.Equal(MonitorStatus.Degraded, shown.Severity);      // not the incident-wide Down
        Assert.Equal(["acme-web"], shown.AffectedMonitors);
    }

    [Fact]
    public async Task A_page_whose_own_outage_has_aged_out_stops_showing_the_incident()
    {
        // The consequence of scoping the timeline: the linger cutoff has to be re-applied to the page's
        // own resolution. Otherwise an incident held open by another tenant would pin a notice here for
        // as long as their outage lasted, however long ago ours ended.
        await using var tdb = await TestDatabase.CreateAsync();
        var mine = await tdb.SeedMonitorAsync("acme-web");
        var theirs = await tdb.SeedMonitorAsync("competitor-web");
        var pageId = await SeedStatusPageAsync(tdb, "acme", mine);

        var now = DateTime.UtcNow;
        await using (var db = tdb.CreateDbContext())
        {
            var incident = new Incident
            {
                Title = "competitor-web",
                StartedAt = now.AddDays(-30),
                LastEventAt = now.AddDays(-30),
                ResolvedAt = null,
                Severity = MonitorStatus.Down,
                MonitorCount = 2,
                Published = true,
            };
            incident.Events.Add(new MonitorEvent
            {
                MonitorId = theirs, StartedAt = now.AddDays(-30), ResolvedAt = null,
                FromStatus = MonitorStatus.Up, ToStatus = MonitorStatus.Down,
            });
            incident.Events.Add(new MonitorEvent
            {
                MonitorId = mine, StartedAt = now.AddDays(-30), ResolvedAt = now.AddDays(-29),
                FromStatus = MonitorStatus.Up, ToStatus = MonitorStatus.Down,
            });
            db.Incidents.Add(incident);
            await db.SaveChangesAsync();
        }

        Assert.Empty(await Incidents(tdb).GetForStatusPageAsync(pageId, now, TimeSpan.FromDays(7)));
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

        Assert.True(await svc.AddUpdateAsync(id, UserRole.Editor, IncidentUpdateKind.Investigating, "Looking into it.", null, now.AddMinutes(-25)));
        Assert.True(await svc.AddUpdateAsync(id, UserRole.Editor, IncidentUpdateKind.Identified, "Bad deploy.", null, now.AddMinutes(-10)));
        // An empty note is refused rather than published as a blank entry.
        Assert.False(await svc.AddUpdateAsync(id, UserRole.Editor, IncidentUpdateKind.Monitoring, "   ", null, now));

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

        Assert.True(await svc.SetPublishedAsync(id, UserRole.Editor, false));
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
