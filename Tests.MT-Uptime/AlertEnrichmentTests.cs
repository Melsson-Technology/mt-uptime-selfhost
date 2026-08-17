using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Incidents;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Notifications;

namespace MT.Uptime.Tests;

/// <summary>
/// Covers what the alert actually says. Correlation that only shows up in the dashboard is worth little:
/// the person being paged is reading a notification, so the incident has to reach the message body.
/// </summary>
public class AlertEnrichmentTests
{
    private static NotificationEvent Down(int monitorId = 1, string name = "acme-web") =>
        new(monitorId, name, MonitorStatus.Down, MonitorStatus.Up,
            new DateTime(2026, 8, 17, 3, 0, 0, DateTimeKind.Utc), "Connection refused", null, NotifyKind.Down);

    private static IncidentSummary Incident(int monitorCount, params string[] others) =>
        new(12, monitorCount, "203.0.113.10", others, new DateTime(2026, 8, 17, 2, 55, 0, DateTimeKind.Utc), false);

    // --- Rendering ---------------------------------------------------------------------------------

    [Fact]
    public void A_correlated_alert_says_it_is_one_of_many_and_where()
    {
        var evt = Down() with { Incident = Incident(20, "acme-api", "shop", "blog") };

        var text = NotificationRenderer.PlainText(evt);

        Assert.Contains("Part of incident #12: 20 monitors are affected on 203.0.113.10.", text);
        Assert.Contains("Also affected: acme-api, shop, blog.", text);
        // The subject carries it too — on a phone that is often all anyone reads.
        Assert.Contains("(+19 more)", NotificationRenderer.Subject(evt));
    }

    [Fact]
    public void A_single_monitor_alert_mentions_no_incident_at_all()
    {
        // "Incident #12 affecting 1 monitor" is noise dressed as information.
        var evt = Down() with { Incident = Incident(1) };

        var text = NotificationRenderer.PlainText(evt);

        Assert.DoesNotContain("incident", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Also affected", text);
        Assert.DoesNotContain("+0 more", NotificationRenderer.Subject(evt));
    }

    [Fact]
    public void An_alert_with_no_context_renders_exactly_as_before()
    {
        var text = NotificationRenderer.PlainText(Down());

        Assert.Contains("acme-web is DOWN.", text);
        Assert.Contains("Detail: Connection refused", text);
        Assert.DoesNotContain("incident", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("[MT-Uptime] DOWN: acme-web", NotificationRenderer.Subject(Down()));
    }

    [Fact]
    public void The_also_affected_list_is_capped()
    {
        var others = Enumerable.Range(1, 30).Select(i => $"site-{i}").ToArray();
        var evt = Down() with { Incident = Incident(31, others) };

        var text = NotificationRenderer.PlainText(evt);

        Assert.Contains("site-8", text);
        Assert.DoesNotContain("site-9,", text);
        Assert.Contains("and 22 more.", text);
    }

    [Fact]
    public void An_acknowledged_incident_is_flagged()
    {
        var evt = Down() with
        {
            Incident = new IncidentSummary(12, 5, "203.0.113.10", ["b"], DateTime.UtcNow, Acknowledged: true),
        };

        Assert.Contains("This incident has been acknowledged.", NotificationRenderer.PlainText(evt));
    }

    [Fact]
    public void Diagnostics_answer_what_broke()
    {
        var evt = Down() with
        {
            Enrichment = new AlertEnrichment("203.0.113.10", "503", [95, 102, 4100], null),
        };

        var text = NotificationRenderer.PlainText(evt);

        Assert.Contains("Last response code: 503", text);
        Assert.Contains("Resolved to: 203.0.113.10", text);
        Assert.Contains("Recent response times (ms, oldest first): 95, 102, 4,100", text);
    }

    [Fact]
    public void A_certificate_is_mentioned_only_when_it_is_actually_a_clue()
    {
        var at = new DateTime(2026, 8, 17, 3, 0, 0, DateTimeKind.Utc);

        // Healthy and far off: the enricher never attaches it, so nothing is rendered.
        Assert.False(AlertEnrichment.IsWorthMentioning(at.AddDays(200), at));

        var expiring = Down() with { Enrichment = new AlertEnrichment(null, null, [], at.AddDays(3)) };
        Assert.Contains("Certificate expires in 3 day(s)", NotificationRenderer.PlainText(expiring));

        var expired = Down() with { Enrichment = new AlertEnrichment(null, null, [], at.AddDays(-2)) };
        Assert.Contains("Certificate EXPIRED 2 day(s) ago", NotificationRenderer.PlainText(expired));
    }

    [Fact]
    public void Html_carries_the_same_context_and_escapes_it()
    {
        var evt = Down() with { Incident = Incident(3, "a & b") };

        var html = NotificationRenderer.Html(evt);

        Assert.Contains("Part of incident #12", html);
        Assert.Contains("a &amp; b", html);
    }

    // --- Gathering ---------------------------------------------------------------------------------

    private static AlertEnricher Enricher(TestDatabase tdb) =>
        new(tdb,
            new CorrelationKeyResolver(NullLogger<CorrelationKeyResolver>.Instance)
            {
                Lookup = (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("203.0.113.10") }),
            },
            NullLogger<AlertEnricher>.Instance);

    [Fact]
    public async Task Recent_timings_and_the_last_status_code_come_from_the_heartbeat_history()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync("web", MonitorType.Http, """{"Url":"https://web.example.com/"}""");

        var start = DateTime.UtcNow.AddMinutes(-10);
        await tdb.AddBeatsAsync(id, start, MonitorStatus.Up, 3, ms: 120);

        await using (var db = tdb.CreateDbContext())
        {
            db.Heartbeats.Add(new Heartbeat
            {
                MonitorId = id,
                Timestamp = DateTime.UtcNow,
                Status = MonitorStatus.Down,
                StatusCode = "503",
            });
            await db.SaveChangesAsync();
        }

        var enriched = await Enricher(tdb).EnrichAsync(Down(id, "web"), incident: null);

        Assert.NotNull(enriched.Enrichment);
        Assert.Equal("503", enriched.Enrichment!.LastStatusCode);
        Assert.Equal("203.0.113.10", enriched.Enrichment.ResolvedAddress);
        Assert.Equal([120, 120, 120], enriched.Enrichment.RecentResponseTimesMs);
        Assert.Null(enriched.Incident);
    }

    [Fact]
    public async Task The_alerting_monitor_is_left_out_of_its_own_also_affected_list()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("acme-web");
        var b = await tdb.SeedMonitorAsync("acme-api");

        var now = DateTime.UtcNow;
        Incident incident;
        await using (var db = tdb.CreateDbContext())
        {
            incident = new Incident
            {
                Title = "acme-web",
                CorrelationKey = "ip:203.0.113.10",
                StartedAt = now,
                LastEventAt = now,
                Severity = MonitorStatus.Down,
                MonitorCount = 2,
            };
            incident.Events.Add(new MonitorEvent { MonitorId = a, StartedAt = now, ToStatus = MonitorStatus.Down });
            incident.Events.Add(new MonitorEvent { MonitorId = b, StartedAt = now, ToStatus = MonitorStatus.Down });
            db.Incidents.Add(incident);
            await db.SaveChangesAsync();

            incident = await db.Incidents.Include(i => i.Events).ThenInclude(e => e.Monitor)
                .FirstAsync(i => i.Id == incident.Id);
        }

        var enriched = await Enricher(tdb).EnrichAsync(Down(a, "acme-web"), incident);

        Assert.NotNull(enriched.Incident);
        Assert.Equal(["acme-api"], enriched.Incident!.OtherAffectedMonitors);
        // The prefix is internal plumbing; the alert shows the address itself.
        Assert.Equal("203.0.113.10", enriched.Incident.SharedInfrastructure);
    }

    [Fact]
    public async Task A_monitor_not_yet_attached_still_counts_itself()
    {
        // The alert is built concurrently with the writer attaching this monitor, so the incident is
        // located by correlation key and its stored count is one short. Left uncorrected that prints
        // "2 monitors are affected" directly above a list naming two *others*.
        await using var tdb = await TestDatabase.CreateAsync();
        var joining = await tdb.SeedMonitorAsync("gamma");
        var a = await tdb.SeedMonitorAsync("alpha");
        var b = await tdb.SeedMonitorAsync("beta");

        var now = DateTime.UtcNow;
        Incident incident;
        await using (var db = tdb.CreateDbContext())
        {
            incident = new Incident
            {
                Title = "alpha",
                CorrelationKey = "ip:203.0.113.10",
                StartedAt = now,
                LastEventAt = now,
                Severity = MonitorStatus.Down,
                MonitorCount = 2,     // gamma is not a member yet
            };
            incident.Events.Add(new MonitorEvent { MonitorId = a, StartedAt = now, ToStatus = MonitorStatus.Down });
            incident.Events.Add(new MonitorEvent { MonitorId = b, StartedAt = now, ToStatus = MonitorStatus.Down });
            db.Incidents.Add(incident);
            await db.SaveChangesAsync();

            incident = await db.Incidents.Include(i => i.Events).ThenInclude(e => e.Monitor)
                .FirstAsync(i => i.Id == incident.Id);
        }

        var enriched = await Enricher(tdb).EnrichAsync(Down(joining, "gamma"), incident);

        Assert.Equal(3, enriched.Incident!.MonitorCount);
        Assert.Equal(2, enriched.Incident.OtherAffectedMonitors.Count);
        // The count and the list must agree: others + the one being alerted on.
        Assert.Equal(enriched.Incident.OtherAffectedMonitors.Count + 1, enriched.Incident.MonitorCount);
        Assert.Contains("3 monitors are affected", NotificationRenderer.PlainText(enriched));
    }

    [Fact]
    public async Task A_missing_monitor_degrades_the_alert_rather_than_stopping_it()
    {
        // Enrichment is a nice-to-have. An alert that says less always beats one that never arrives.
        await using var tdb = await TestDatabase.CreateAsync();

        var enriched = await Enricher(tdb).EnrichAsync(Down(monitorId: 9999, name: "vanished"), incident: null);

        Assert.Equal("vanished", enriched.MonitorName);
        Assert.Contains("vanished is DOWN.", NotificationRenderer.PlainText(enriched));
    }
}
