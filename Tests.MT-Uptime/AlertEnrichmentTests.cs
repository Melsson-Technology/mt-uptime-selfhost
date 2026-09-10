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

        var text = NotificationRenderer.PlainText(evt, AlertVerbosity.Rich);

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

        var text = NotificationRenderer.PlainText(evt, AlertVerbosity.Rich);

        Assert.DoesNotContain("incident", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Also affected", text);
        Assert.DoesNotContain("+0 more", NotificationRenderer.Subject(evt));
    }

    [Fact]
    public void An_alert_with_no_context_renders_exactly_as_before()
    {
        var text = NotificationRenderer.PlainText(Down(), AlertVerbosity.Rich);

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

        var text = NotificationRenderer.PlainText(evt, AlertVerbosity.Rich);

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

        Assert.Contains("This incident has been acknowledged.", NotificationRenderer.PlainText(evt, AlertVerbosity.Rich));
    }

    [Fact]
    public void Diagnostics_answer_what_broke()
    {
        var at = new DateTime(2026, 8, 17, 3, 0, 0, DateTimeKind.Utc);
        var evt = Down() with
        {
            Enrichment = new AlertEnrichment(
                "203.0.113.10", "200", 46, at.AddMinutes(-1), [95, 102, 4100], null, at.AddDays(-4).AddHours(-3)),
        };

        var text = NotificationRenderer.PlainText(evt, AlertVerbosity.Rich);

        Assert.Contains("Last good response: 200, 46 ms, 1m ago", text);
        Assert.Contains("Previous state held for 4d 3h.", text);
        Assert.Contains("Resolved to: 203.0.113.10", text);
        Assert.Contains("Recent response times before this (ms, oldest first): 95, 102, 4,100", text);
    }

    [Fact]
    public void A_cloudflare_status_code_is_explained_rather_than_just_quoted()
    {
        // The alert that prompted this work said only "Unexpected status 521". 521 is Cloudflare's, not
        // an HTTP standard code, and it means the edge is healthy while the origin refused the
        // connection — a different 03:00 call from "the website is down".
        var evt = Down() with { Message = "Unexpected status 521", StatusCode = "521" };

        var text = NotificationRenderer.PlainText(evt, AlertVerbosity.Rich);

        Assert.Contains("Detail: Unexpected status 521", text);
        Assert.Contains("What 521 means: Cloudflare is answering, but the origin refused the connection", text);
    }

    [Fact]
    public void An_ordinary_status_code_gets_no_gloss_and_a_non_numeric_one_does_not_throw()
    {
        // Glossing 404 teaches nobody anything and pads every alert, which trains people to skip the
        // detail lines entirely — the same argument that hides healthy certificates.
        Assert.Null(StatusCodeGloss.For("404"));
        Assert.Null(StatusCodeGloss.For("200"));

        // TlsChecker stores "expired" in this field; StatusCode is free-form across monitor types.
        Assert.Null(StatusCodeGloss.For("expired"));
        Assert.Null(StatusCodeGloss.For(null));

        Assert.DoesNotContain("What ", NotificationRenderer.PlainText(Down() with { StatusCode = "404" }, AlertVerbosity.Rich));
    }

    [Fact]
    public void A_certificate_is_mentioned_only_when_it_is_actually_a_clue()
    {
        var at = new DateTime(2026, 8, 17, 3, 0, 0, DateTimeKind.Utc);

        // Healthy and far off: the enricher never attaches it, so nothing is rendered.
        Assert.False(AlertEnrichment.IsWorthMentioning(at.AddDays(200), at));

        var expiring = Down() with { Enrichment = new AlertEnrichment(null, null, null, null, [], at.AddDays(3)) };
        Assert.Contains("Certificate expires in 3 day(s)", NotificationRenderer.PlainText(expiring, AlertVerbosity.Rich));

        var expired = Down() with { Enrichment = new AlertEnrichment(null, null, null, null, [], at.AddDays(-2)) };
        Assert.Contains("Certificate EXPIRED 2 day(s) ago", NotificationRenderer.PlainText(expired, AlertVerbosity.Rich));
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

    /// <summary>
    /// The event time every gathering test anchors to. The old version of these tests seeded beats at
    /// <c>DateTime.UtcNow</c> while the event was stamped 2026-08-17, so the two were never related and
    /// "newest row wins" was the only rule being exercised.
    /// </summary>
    private static readonly DateTime AlertAt = new(2026, 8, 17, 3, 0, 0, DateTimeKind.Utc);

    private static async Task SeedBeatAsync(
        TestDatabase tdb, int monitorId, DateTime at, MonitorStatus status,
        string? code = null, double? ms = null, bool important = false)
    {
        await using var db = tdb.CreateDbContext();
        db.Heartbeats.Add(new Heartbeat
        {
            MonitorId = monitorId,
            Timestamp = at,
            Status = status,
            StatusCode = code,
            ResponseTimeMs = ms,
            Important = important,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The regression that started this work. The heartbeat writer flushes asynchronously, so at
    /// dispatch the failing beat may or may not be in the table — and the enricher used to report
    /// "the newest heartbeat's code" as <c>Last response code</c>. Whoever won the race decided whether
    /// the alert showed the failure or the state before it, which is how a real alert came to read
    /// "Detail: Unexpected status 521" directly above "Last response code: 200".
    /// <para>
    /// Both orderings are asserted to produce the same output. Run this against the old
    /// <c>OrderByDescending(...).First()</c> and the flushed case fails.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]   // the failing beat won the race and is already in the table
    [InlineData(false)]  // it has not been flushed yet
    public async Task The_last_good_response_does_not_depend_on_racing_the_heartbeat_writer(bool failingBeatFlushed)
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync("web", MonitorType.Http, """{"Url":"https://web.example.com/"}""");

        // A healthy run, then the failure. The runner stamps the failing beat and the event from the
        // same instant, so the flushed case seeds it at exactly AlertAt.
        await SeedBeatAsync(tdb, id, AlertAt.AddMinutes(-3), MonitorStatus.Up, "200", 45);
        await SeedBeatAsync(tdb, id, AlertAt.AddMinutes(-2), MonitorStatus.Up, "200", 64);
        await SeedBeatAsync(tdb, id, AlertAt.AddMinutes(-1), MonitorStatus.Up, "200", 46);

        if (failingBeatFlushed)
            await SeedBeatAsync(tdb, id, AlertAt, MonitorStatus.Down, "521", 21556);

        var enriched = await Enricher(tdb).EnrichAsync(Down(id, "web") with { At = AlertAt }, incident: null);

        Assert.NotNull(enriched.Enrichment);

        // The last *good* response, either way — never the 521 that the alert's own Detail line carries.
        Assert.Equal("200", enriched.Enrichment!.LastGoodStatusCode);
        Assert.Equal(46, enriched.Enrichment.LastGoodResponseTimeMs);
        Assert.Equal(AlertAt.AddMinutes(-1), enriched.Enrichment.LastGoodAt);

        // And the baseline series excludes the outlier, so the healthy numbers stay legible rather than
        // being flattened by a 21,556 ms tail.
        Assert.Equal([45, 64, 46], enriched.Enrichment.RecentResponseTimesMs);

        Assert.Equal("203.0.113.10", enriched.Enrichment.ResolvedAddress);
        Assert.Null(enriched.Incident);
    }

    [Fact]
    public async Task The_alert_says_how_long_the_previous_state_had_held()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync("web", MonitorType.Http, """{"Url":"https://web.example.com/"}""");

        // Important marks a state transition, so this is when the run of Up began.
        await SeedBeatAsync(tdb, id, AlertAt.AddDays(-4).AddHours(-3), MonitorStatus.Up, "200", 50, important: true);
        await SeedBeatAsync(tdb, id, AlertAt.AddMinutes(-1), MonitorStatus.Up, "200", 46);

        var enriched = await Enricher(tdb).EnrichAsync(Down(id, "web") with { At = AlertAt }, incident: null);

        Assert.Equal(AlertAt.AddDays(-4).AddHours(-3), enriched.Enrichment!.PreviousStateSince);
        // A monitor solid for four days failing is a different event from one flapping all evening.
        Assert.Contains("Previous state held for 4d 3h.", NotificationRenderer.PlainText(enriched, AlertVerbosity.Rich));
    }

    [Fact]
    public async Task A_monitor_that_has_never_succeeded_simply_omits_the_last_good_line()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync("web", MonitorType.Http, """{"Url":"https://web.example.com/"}""");

        await SeedBeatAsync(tdb, id, AlertAt.AddMinutes(-1), MonitorStatus.Down, "521", 21556);

        var enriched = await Enricher(tdb).EnrichAsync(Down(id, "web") with { At = AlertAt }, incident: null);

        Assert.Null(enriched.Enrichment!.LastGoodStatusCode);
        Assert.DoesNotContain("Last good response", NotificationRenderer.PlainText(enriched, AlertVerbosity.Rich));
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
        Assert.Contains("3 monitors are affected", NotificationRenderer.PlainText(enriched, AlertVerbosity.Rich));
    }

    [Fact]
    public async Task A_missing_monitor_degrades_the_alert_rather_than_stopping_it()
    {
        // Enrichment is a nice-to-have. An alert that says less always beats one that never arrives.
        await using var tdb = await TestDatabase.CreateAsync();

        var enriched = await Enricher(tdb).EnrichAsync(Down(monitorId: 9999, name: "vanished"), incident: null);

        Assert.Equal("vanished", enriched.MonitorName);
        Assert.Contains("vanished is DOWN.", NotificationRenderer.PlainText(enriched, AlertVerbosity.Rich));
    }
}
