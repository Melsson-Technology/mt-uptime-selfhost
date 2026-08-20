using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Incidents;
using MT.Uptime.Core.Maintenance;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Notifications;

namespace MT.Uptime.Tests;

/// <summary>
/// Covers the incident layer end to end through <see cref="HeartbeatWriter"/> — the single writer that
/// owns the resolve → attach → close ordering. Driving the real writer rather than
/// <see cref="IncidentService"/> alone is deliberate: the ordering is the part that is easy to get wrong
/// and it lives in the writer, not the service.
/// </summary>
public class IncidentTests
{
    private const string HostA = "203.0.113.10";
    private const string HostB = "203.0.113.20";

    /// <summary>A resolver with DNS stubbed out, so correlation is deterministic and offline.</summary>
    private static CorrelationKeyResolver Resolver(Dictionary<string, string> hostToIp) =>
        new(NullLogger<CorrelationKeyResolver>.Instance)
        {
            Lookup = (host, _) => Task.FromResult(
                hostToIp.TryGetValue(host, out var ip) ? new[] { IPAddress.Parse(ip) } : Array.Empty<IPAddress>()),
        };

    private static IncidentService Incidents(TestDatabase tdb, CorrelationKeyResolver resolver, int windowMinutes = 10) =>
        new(tdb, resolver, Options.Create(new EngineOptions { IncidentCorrelationWindowMinutes = windowMinutes }),
            NullLogger<IncidentService>.Instance);

    private static string HttpConfig(string url) => $$"""{"Url":"{{url}}"}""";

    private static CheckOutcome Down(int monitorId, DateTime at, MonitorStatus from = MonitorStatus.Up) =>
        new(monitorId, at, MonitorStatus.Down, null, null, "boom", true, 1, null,
            EventAction.Open, from, MonitorStatus.Down);

    private static CheckOutcome Up(int monitorId, DateTime at, MonitorStatus from = MonitorStatus.Down) =>
        new(monitorId, at, MonitorStatus.Up, 20, null, null, true, 0, null,
            EventAction.Resolve, from, MonitorStatus.Up);

    /// <summary>Runs outcomes through a real writer and waits for them to land.</summary>
    private static async Task WriteAsync(TestDatabase tdb, IncidentService incidents, params CheckOutcome[] outcomes)
    {
        // Baselined, not absolute: a test may call this more than once against the same database, and
        // counting from zero would see the earlier run's beats and return before this one had landed.
        int before;
        await using (var seed = tdb.CreateDbContext())
            before = await seed.Heartbeats.CountAsync();

        var writer = new HeartbeatWriter(tdb, incidents, new MaintenanceWindowService(tdb),
            NullLogger<HeartbeatWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);
        try
        {
            foreach (var o in outcomes) writer.Enqueue(o);

            // The channel has a single reader draining in order, so the last heartbeat appearing means
            // every earlier outcome has been fully applied.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                await using var db = tdb.CreateDbContext();
                if (await db.Heartbeats.CountAsync() >= before + outcomes.Length) return;
                await Task.Delay(25);
            }

            throw new TimeoutException("Heartbeats did not drain within 15s.");
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<List<Incident>> IncidentsInAsync(TestDatabase tdb)
    {
        await using var db = tdb.CreateDbContext();
        return await db.Incidents.Include(i => i.Events).OrderBy(i => i.Id).ToListAsync();
    }

    // --- Correlation key ---------------------------------------------------------------------------

    [Fact]
    public async Task Correlation_key_prefers_the_resolved_address()
    {
        var r = Resolver(new() { ["a.example.com"] = HostA });
        var key = await r.GetKeyAsync(MonitorType.Http, HttpConfig("https://a.example.com/health"));
        Assert.Equal($"ip:{HostA}", key);
    }

    [Fact]
    public async Task Two_hostnames_on_one_address_produce_one_key()
    {
        // The differentiator in miniature: different names, same box, therefore the same key.
        var r = Resolver(new() { ["a.example.com"] = HostA, ["b.example.net"] = HostA });

        var first = await r.GetKeyAsync(MonitorType.Http, HttpConfig("https://a.example.com/"));
        var second = await r.GetKeyAsync(MonitorType.Tcp, """{"Host":"b.example.net","Port":443}""");

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Unresolvable_host_falls_back_to_the_hostname()
    {
        var r = Resolver([]);
        var key = await r.GetKeyAsync(MonitorType.Http, HttpConfig("https://nowhere.example/"));
        Assert.Equal("host:nowhere.example", key);
    }

    [Fact]
    public async Task A_failed_lookup_never_costs_the_heartbeat_or_the_event()
    {
        // The decisive one. The resolver used to catch only SocketException and OperationCanceledException,
        // but .NET rejects an over-long host with ArgumentOutOfRangeException — and that escape landed in
        // HeartbeatWriter, taking the beat, the event and the incident with it before SaveChangesAsync
        // ever ran. Reaching the assertions at all is half of what this asserts.
        await using var tdb = await TestDatabase.CreateAsync();
        var id = await tdb.SeedMonitorAsync("ssh", MonitorType.Tcp, """{"Host":"a.example.com","Port":22}""");
        var resolver = new CorrelationKeyResolver(NullLogger<CorrelationKeyResolver>.Instance)
        {
            Lookup = (_, _) => throw new ArgumentOutOfRangeException("hostName"),
        };

        await WriteAsync(tdb, Incidents(tdb, resolver), Down(id, DateTime.UtcNow));

        var incident = Assert.Single(await IncidentsInAsync(tdb));
        Assert.Equal("host:a.example.com", incident.CorrelationKey);   // the documented fallback
        Assert.Single(incident.Events);
    }

    [Fact]
    public async Task A_host_too_long_to_be_a_name_is_not_correlated()
    {
        var r = Resolver(new() { ["a.example.com"] = HostA });
        var overlong = string.Join('.', Enumerable.Repeat(new string('a', 20), 13));   // 272 characters

        // Positive control first, so this cannot pass by the resolver having stopped resolving anything.
        Assert.Equal($"ip:{HostA}", await r.GetKeyAsync(MonitorType.Tcp, """{"Host":"a.example.com","Port":22}"""));
        Assert.Null(await r.GetKeyAsync(MonitorType.Tcp, $$"""{"Host":"{{overlong}}","Port":22}"""));
    }

    [Fact]
    public async Task Shutdown_is_the_one_thing_the_resolver_will_not_swallow()
    {
        // The catch is deliberately broad, which makes it worth pinning that it stops short of pretending
        // a cancelled request resolved. The two-second budget cancels its own linked token, not this one.
        var r = new CorrelationKeyResolver(NullLogger<CorrelationKeyResolver>.Instance)
        {
            Lookup = (_, _) => throw new OperationCanceledException(),
        };
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            r.GetKeyAsync(MonitorType.Tcp, """{"Host":"a.example.com","Port":22}""", cancelled.Token));
    }

    [Fact]
    public async Task Address_literal_needs_no_lookup()
    {
        var r = new CorrelationKeyResolver(NullLogger<CorrelationKeyResolver>.Instance)
        {
            Lookup = (_, _) => throw new InvalidOperationException("must not resolve a literal"),
        };

        Assert.Equal($"ip:{HostA}", await r.GetKeyAsync(MonitorType.Tcp, $$"""{"Host":"{{HostA}}","Port":22}"""));
    }

    [Fact]
    public async Task Push_monitors_have_no_correlation_key()
    {
        // Nothing tells us where a push target runs — it contacts us.
        var r = Resolver([]);
        Assert.Null(await r.GetKeyAsync(MonitorType.Push, "{}"));
    }

    [Fact]
    public async Task Dns_monitor_keys_on_its_resolver_not_the_queried_name()
    {
        var r = Resolver([]);

        // The queried name is the thing under test; the resolver is the shared infrastructure.
        Assert.Equal($"ip:{HostB}",
            await r.GetKeyAsync(MonitorType.Dns, $$"""{"Hostname":"example.com","Resolver":"{{HostB}}"}"""));

        // System resolver: a real dependency, but not one we can name.
        Assert.Null(await r.GetKeyAsync(MonitorType.Dns, """{"Hostname":"example.com"}"""));
    }

    // --- Grouping ----------------------------------------------------------------------------------

    [Fact]
    public async Task Monitors_sharing_a_host_produce_one_incident()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var b = await tdb.SeedMonitorAsync("site-b", MonitorType.Http, HttpConfig("https://b.example.net/"));
        var svc = Incidents(tdb, Resolver(new() { ["a.example.com"] = HostA, ["b.example.net"] = HostA }));

        var t0 = DateTime.UtcNow;
        await WriteAsync(tdb, svc, Down(a, t0), Down(b, t0.AddSeconds(30)));

        var incident = Assert.Single(await IncidentsInAsync(tdb));
        Assert.Equal($"ip:{HostA}", incident.CorrelationKey);
        Assert.Equal(2, incident.MonitorCount);
        Assert.Equal(2, incident.Events.Count);
        Assert.True(incident.IsOpen);
    }

    [Fact]
    public async Task Monitors_on_different_hosts_stay_separate()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var b = await tdb.SeedMonitorAsync("site-b", MonitorType.Http, HttpConfig("https://b.example.net/"));
        var svc = Incidents(tdb, Resolver(new() { ["a.example.com"] = HostA, ["b.example.net"] = HostB }));

        var t0 = DateTime.UtcNow;
        await WriteAsync(tdb, svc, Down(a, t0), Down(b, t0.AddSeconds(30)));

        Assert.Equal(2, (await IncidentsInAsync(tdb)).Count);
    }

    [Fact]
    public async Task Failure_outside_the_window_opens_a_new_incident()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var b = await tdb.SeedMonitorAsync("site-b", MonitorType.Http, HttpConfig("https://b.example.net/"));
        var svc = Incidents(tdb, Resolver(new() { ["a.example.com"] = HostA, ["b.example.net"] = HostA }), windowMinutes: 10);

        var t0 = DateTime.UtcNow;
        // Same host, but 20 minutes later: a still-open incident must not act as a magnet indefinitely.
        await WriteAsync(tdb, svc, Down(a, t0), Down(b, t0.AddMinutes(20)));

        Assert.Equal(2, (await IncidentsInAsync(tdb)).Count);
    }

    // --- Lifecycle ---------------------------------------------------------------------------------

    [Fact]
    public async Task Incident_stays_open_until_the_last_member_recovers()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var b = await tdb.SeedMonitorAsync("site-b", MonitorType.Http, HttpConfig("https://b.example.net/"));
        var svc = Incidents(tdb, Resolver(new() { ["a.example.com"] = HostA, ["b.example.net"] = HostA }));

        var t0 = DateTime.UtcNow;
        await WriteAsync(tdb, svc, Down(a, t0), Down(b, t0.AddSeconds(30)), Up(a, t0.AddMinutes(2)));

        var incident = Assert.Single(await IncidentsInAsync(tdb));
        Assert.True(incident.IsOpen);   // b is still down

        await WriteAsync(tdb, svc, Up(b, t0.AddMinutes(5)));

        incident = Assert.Single(await IncidentsInAsync(tdb));
        Assert.False(incident.IsOpen);
        Assert.Equal(300, incident.DurationSeconds);
    }

    [Fact]
    public async Task Escalation_from_degraded_to_down_stays_one_incident()
    {
        // The regression this ordering exists for: ResolveAndOpen closes one event and opens another in
        // the same beat. Judged in between, every member looks resolved and one outage becomes two.
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var svc = Incidents(tdb, Resolver(new() { ["a.example.com"] = HostA }));

        var t0 = DateTime.UtcNow;
        var degraded = new CheckOutcome(a, t0, MonitorStatus.Degraded, 9000, null, "slow", true, 3, null,
            EventAction.Open, MonitorStatus.Up, MonitorStatus.Degraded);
        var escalate = new CheckOutcome(a, t0.AddMinutes(1), MonitorStatus.Down, null, null, "boom", true, 1, null,
            EventAction.ResolveAndOpen, MonitorStatus.Degraded, MonitorStatus.Down);

        await WriteAsync(tdb, svc, degraded, escalate);

        var incident = Assert.Single(await IncidentsInAsync(tdb));
        Assert.True(incident.IsOpen);
        Assert.Equal(2, incident.Events.Count);
        Assert.Equal(1, incident.MonitorCount);
        // Severity tracks the worst state reached, so the incident reads as an outage, not a slowdown.
        Assert.Equal(MonitorStatus.Down, incident.Severity);
    }

    [Fact]
    public async Task Severity_is_not_walked_back_when_a_milder_member_joins()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var b = await tdb.SeedMonitorAsync("site-b", MonitorType.Http, HttpConfig("https://b.example.net/"));
        var svc = Incidents(tdb, Resolver(new() { ["a.example.com"] = HostA, ["b.example.net"] = HostA }));

        var t0 = DateTime.UtcNow;
        var slow = new CheckOutcome(b, t0.AddSeconds(30), MonitorStatus.Degraded, 9000, null, "slow", true, 3, null,
            EventAction.Open, MonitorStatus.Up, MonitorStatus.Degraded);

        await WriteAsync(tdb, svc, Down(a, t0), slow);

        var incident = Assert.Single(await IncidentsInAsync(tdb));
        Assert.Equal(MonitorStatus.Down, incident.Severity);
    }

    // --- Acknowledgement, snooze and suppression ---------------------------------------------------

    private static AlertSuppressionService Suppression(
        TestDatabase tdb, CorrelationKeyResolver resolver, int windowMinutes = 10) =>
        new(Incidents(tdb, resolver, windowMinutes), new MaintenanceWindowService(tdb));

    private static NotificationEvent Alert(int monitorId, DateTime at, NotifyKind kind) =>
        new(monitorId, $"monitor-{monitorId}", MonitorStatus.Down, MonitorStatus.Up, at, "boom", null, kind);

    [Fact]
    public async Task Acknowledging_stops_the_repeat_alerts()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var resolver = Resolver(new() { ["a.example.com"] = HostA });
        var svc = Incidents(tdb, resolver);
        var suppression = Suppression(tdb, resolver);

        var t0 = DateTime.UtcNow;
        await WriteAsync(tdb, svc, Down(a, t0));

        // Before acknowledgement the repeat alert goes out.
        Assert.False((await suppression.EvaluateAsync(Alert(a, t0.AddMinutes(5), NotifyKind.ResendDown))).Suppress);

        var incident = Assert.Single(await IncidentsInAsync(tdb));
        Assert.True(await svc.AcknowledgeAsync(incident.Id, UserRole.Editor, userId: null, t0.AddMinutes(6)));

        Assert.True((await suppression.EvaluateAsync(Alert(a, t0.AddMinutes(10), NotifyKind.ResendDown))).Suppress);
    }

    [Fact]
    public async Task Recovery_is_never_suppressed_even_when_acknowledged()
    {
        // The PagerDuty rule: a channel that opened a remote incident must always get its resolve, or the
        // remote incident is stranded open with no way back except a human closing it by hand.
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var resolver = Resolver(new() { ["a.example.com"] = HostA });
        var svc = Incidents(tdb, resolver);
        var suppression = Suppression(tdb, resolver);

        var t0 = DateTime.UtcNow;
        await WriteAsync(tdb, svc, Down(a, t0));
        var incident = Assert.Single(await IncidentsInAsync(tdb));
        await svc.AcknowledgeAsync(incident.Id, UserRole.Editor, userId: null, t0.AddMinutes(1));
        await svc.SnoozeAsync(incident.Id, UserRole.Editor, TimeSpan.FromHours(4), t0.AddMinutes(1));

        var decision = await suppression.EvaluateAsync(Alert(a, t0.AddMinutes(5), NotifyKind.Up));
        Assert.False(decision.Suppress);
    }

    [Fact]
    public async Task Snooze_expires_and_alerting_resumes()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var resolver = Resolver(new() { ["a.example.com"] = HostA });
        var svc = Incidents(tdb, resolver);
        var suppression = Suppression(tdb, resolver);

        var t0 = DateTime.UtcNow;
        await WriteAsync(tdb, svc, Down(a, t0));
        var incident = Assert.Single(await IncidentsInAsync(tdb));
        await svc.SnoozeAsync(incident.Id, UserRole.Editor, TimeSpan.FromMinutes(30), t0);

        Assert.True((await suppression.EvaluateAsync(Alert(a, t0.AddMinutes(10), NotifyKind.ResendDown))).Suppress);
        Assert.False((await suppression.EvaluateAsync(Alert(a, t0.AddMinutes(31), NotifyKind.ResendDown))).Suppress);
    }

    [Fact]
    public async Task Acknowledging_a_host_silences_the_next_monitor_to_fail_on_it()
    {
        // The reason acknowledgement is per-incident: having accepted "that box is down", the twenty-first
        // site on it going down is not news. Note b has no incident membership yet at this point — the
        // lookup finds the incident by correlation key, which is the case that makes this work.
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var b = await tdb.SeedMonitorAsync("site-b", MonitorType.Http, HttpConfig("https://b.example.net/"));
        var resolver = Resolver(new() { ["a.example.com"] = HostA, ["b.example.net"] = HostA });
        var svc = Incidents(tdb, resolver);
        var suppression = Suppression(tdb, resolver);

        var t0 = DateTime.UtcNow;
        await WriteAsync(tdb, svc, Down(a, t0));
        var incident = Assert.Single(await IncidentsInAsync(tdb));
        await svc.AcknowledgeAsync(incident.Id, UserRole.Editor, userId: null, t0.AddMinutes(1));

        Assert.True((await suppression.EvaluateAsync(Alert(b, t0.AddMinutes(2), NotifyKind.Down))).Suppress);
    }

    [Fact]
    public async Task A_monitor_elsewhere_is_unaffected_by_the_acknowledgement()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var c = await tdb.SeedMonitorAsync("site-c", MonitorType.Http, HttpConfig("https://c.example.org/"));
        var resolver = Resolver(new() { ["a.example.com"] = HostA, ["c.example.org"] = HostB });
        var svc = Incidents(tdb, resolver);
        var suppression = Suppression(tdb, resolver);

        var t0 = DateTime.UtcNow;
        await WriteAsync(tdb, svc, Down(a, t0));
        var incident = Assert.Single(await IncidentsInAsync(tdb));
        await svc.AcknowledgeAsync(incident.Id, UserRole.Editor, userId: null, t0.AddMinutes(1));

        Assert.False((await suppression.EvaluateAsync(Alert(c, t0.AddMinutes(2), NotifyKind.Down))).Suppress);
    }

    [Fact]
    public async Task Acknowledging_a_closed_incident_reports_failure()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("site-a", MonitorType.Http, HttpConfig("https://a.example.com/"));
        var svc = Incidents(tdb, Resolver(new() { ["a.example.com"] = HostA }));

        var t0 = DateTime.UtcNow;
        await WriteAsync(tdb, svc, Down(a, t0), Up(a, t0.AddMinutes(1)));

        var incident = Assert.Single(await IncidentsInAsync(tdb));
        Assert.False(incident.IsOpen);
        // Silently succeeding here would let the UI report an acknowledgement that changes nothing.
        Assert.False(await svc.AcknowledgeAsync(incident.Id, UserRole.Editor, userId: null, t0.AddMinutes(2)));
    }

    [Fact]
    public async Task Uncorrelatable_monitors_still_get_their_own_incident()
    {
        // No key means no grouping, but every outage must still be an incident so callers never have to
        // special-case the single-monitor path.
        await using var tdb = await TestDatabase.CreateAsync();
        var a = await tdb.SeedMonitorAsync("push-a", MonitorType.Push);
        var b = await tdb.SeedMonitorAsync("push-b", MonitorType.Push);
        var svc = Incidents(tdb, Resolver([]));

        var t0 = DateTime.UtcNow;
        await WriteAsync(tdb, svc, Down(a, t0), Down(b, t0.AddSeconds(30)));

        var all = await IncidentsInAsync(tdb);
        Assert.Equal(2, all.Count);
        Assert.All(all, i => Assert.Null(i.CorrelationKey));
        Assert.All(all, i => Assert.Equal(1, i.MonitorCount));
    }
}
