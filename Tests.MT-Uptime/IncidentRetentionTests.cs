using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests;

/// <summary>
/// Retention for incidents, which nothing pruned until now — <c>Incident</c> and <c>IncidentUpdate</c>
/// grew for the life of an install. Invisible on this instance and unavoidable on somebody's Raspberry
/// Pi two years in, which is a class of defect that only shows up once other people run the thing.
/// </summary>
public class IncidentRetentionTests
{
    private static async Task<long> AddIncidentAsync(
        TestDatabase tdb, DateTime startedAt, DateTime? resolvedAt, int? monitorId = null, int updates = 0)
    {
        await using var db = tdb.CreateDbContext();
        var incident = new Incident
        {
            Title = "something broke",
            StartedAt = startedAt,
            LastEventAt = startedAt,
            ResolvedAt = resolvedAt,
            Severity = MonitorStatus.Down,
        };
        db.Incidents.Add(incident);

        if (monitorId is { } id)
            incident.Events.Add(new MonitorEvent
            {
                MonitorId = id,
                StartedAt = startedAt,
                ResolvedAt = resolvedAt,
                FromStatus = MonitorStatus.Up,
                ToStatus = MonitorStatus.Down,
            });

        for (var i = 0; i < updates; i++)
            incident.Updates.Add(new IncidentUpdate
            {
                Kind = IncidentUpdateKind.Investigating,
                Body = $"update {i}",
                PostedAt = startedAt,
            });

        await db.SaveChangesAsync();
        return incident.Id;
    }

    [Fact]
    public async Task A_resolved_incident_past_the_window_is_pruned_with_its_updates()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var monitorId = await tdb.SeedMonitorAsync("site");

        var old = DateTime.UtcNow.AddDays(-400);
        var oldId = await AddIncidentAsync(tdb, old, old.AddHours(1), monitorId, updates: 2);
        var recent = DateTime.UtcNow.AddDays(-10);
        var recentId = await AddIncidentAsync(tdb, recent, recent.AddHours(1), monitorId, updates: 1);

        var result = await tdb.NewRetention(rawDays: 30).RunCleanupAsync();

        Assert.Equal(1, result.IncidentsPruned);
        await using var db = tdb.CreateDbContext();
        Assert.Null(await db.Incidents.FirstOrDefaultAsync(i => i.Id == oldId));
        Assert.NotNull(await db.Incidents.FirstOrDefaultAsync(i => i.Id == recentId));

        // The updates went with it, and only its own.
        Assert.Empty(await db.IncidentUpdates.Where(u => u.IncidentId == oldId).ToListAsync());
        Assert.Single(await db.IncidentUpdates.Where(u => u.IncidentId == recentId).ToListAsync());
    }

    [Fact]
    public async Task An_open_incident_is_never_pruned_however_old_it_is()
    {
        // A monitor that has been down for two years is an operational problem, not a retention one, and
        // deleting the record of it is the least useful response available.
        await using var tdb = await TestDatabase.CreateAsync();
        var monitorId = await tdb.SeedMonitorAsync("site");
        var id = await AddIncidentAsync(tdb, DateTime.UtcNow.AddDays(-800), resolvedAt: null, monitorId);

        var result = await tdb.NewRetention(rawDays: 30).RunCleanupAsync();

        Assert.Equal(0, result.IncidentsPruned);
        await using var db = tdb.CreateDbContext();
        Assert.NotNull(await db.Incidents.FirstOrDefaultAsync(i => i.Id == id));
    }

    [Fact]
    public async Task Pruning_an_incident_keeps_the_monitor_events_it_grouped()
    {
        // MonitorEvent.IncidentId is SetNull rather than Cascade on purpose: the event is the durable
        // per-monitor record shown on the monitor detail page, and the incident is only a grouping over
        // it. Discarding a grouping must not delete the history it grouped.
        await using var tdb = await TestDatabase.CreateAsync();
        var monitorId = await tdb.SeedMonitorAsync("site");
        var old = DateTime.UtcNow.AddDays(-400);
        await AddIncidentAsync(tdb, old, old.AddHours(1), monitorId);

        await tdb.NewRetention(rawDays: 30).RunCleanupAsync();

        await using var db = tdb.CreateDbContext();
        var events = await db.MonitorEvents.Where(e => e.MonitorId == monitorId).ToListAsync();
        var survivor = Assert.Single(events);
        Assert.Null(survivor.IncidentId);
    }

    [Fact]
    public async Task An_incident_left_with_no_members_by_a_deleted_monitor_is_cleared()
    {
        // Deleting a monitor cascades its events, which can empty an incident of everything it grouped.
        // An *open* one then sits on /incidents forever with nothing under it and no way to clear it,
        // which is the visible half of this bug.
        await using var tdb = await TestDatabase.CreateAsync();
        var monitorId = await tdb.SeedMonitorAsync("doomed");
        var id = await AddIncidentAsync(tdb, DateTime.UtcNow.AddHours(-3), resolvedAt: null, monitorId);

        await using (var db = tdb.CreateDbContext())
        {
            db.Monitors.Remove(await db.Monitors.SingleAsync(m => m.Id == monitorId));
            await db.SaveChangesAsync();

            // The precondition this test exists for: an open incident with no members at all.
            var stranded = await db.Incidents.Include(i => i.Events).SingleAsync(i => i.Id == id);
            Assert.Empty(stranded.Events);
            Assert.Null(stranded.ResolvedAt);
        }

        var result = await tdb.NewRetention(rawDays: 30).RunCleanupAsync();

        Assert.Equal(1, result.IncidentsPruned);
        await using var verify = tdb.CreateDbContext();
        Assert.Null(await verify.Incidents.FirstOrDefaultAsync(i => i.Id == id));
    }

    [Fact]
    public async Task A_member_less_incident_within_the_grace_hour_is_left_alone()
    {
        // The grace exists so that being wrong about "a committed incident always has an event" cannot
        // delete a live incident mid-outage. Assert it, or the grace is one refactor from vanishing as
        // an unexplained constant.
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await AddIncidentAsync(tdb, DateTime.UtcNow.AddMinutes(-5), resolvedAt: null);

        var result = await tdb.NewRetention(rawDays: 30).RunCleanupAsync();

        Assert.Equal(0, result.IncidentsPruned);
        await using var db = tdb.CreateDbContext();
        Assert.NotNull(await db.Incidents.FirstOrDefaultAsync(i => i.Id == id));
    }
}
