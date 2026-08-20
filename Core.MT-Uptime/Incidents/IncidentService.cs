using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Core.Incidents;

/// <summary>
/// Groups per-monitor <see cref="MonitorEvent"/>s into <see cref="Incident"/>s.
/// <para>
/// Every method here mutates the tracked graph and <b>deliberately does not save</b>. The caller
/// (<c>HeartbeatWriter</c>) commits the heartbeat, the event open/resolve and the incident change in one
/// <c>SaveChangesAsync</c>, which is what stops an escalation from half-applying — see the ordering note
/// on <see cref="CloseIfAllResolved"/>.
/// </para>
/// </summary>
public sealed class IncidentService(
    IDbContextFactory<AppDbContext> factory,
    CorrelationKeyResolver keys,
    IOptions<EngineOptions> options,
    ILogger<IncidentService> log)
{
    private TimeSpan CorrelationWindow => TimeSpan.FromMinutes(Math.Max(1, options.Value.IncidentCorrelationWindowMinutes));

    /// <summary>
    /// Whether a role may change an incident: acknowledge, snooze, post an update, or publish one.
    /// <para>
    /// Every method that mutates an incident takes the acting role and consults this, which is
    /// <b>defence in depth and not the primary gate</b> — the gate is <c>&lt;AuthorizeView&gt;</c> on
    /// <c>Incidents.razor</c> and <c>IncidentDetail.razor</c>, and it is correctly placed. The reason to
    /// have both is what the primary gate rests on: an interactive Blazor page does not receive a form
    /// post it can re-validate, it receives an event dispatched to a handler id, and a control never
    /// rendered for a Viewer's circuit has no id to dispatch to. That is almost certainly true and it is
    /// a property of the framework rather than of this application, which is a bad place for the whole
    /// Viewer-to-Editor boundary to live — particularly one <c>SECURITY.md</c> names as a boundary bugs
    /// will be fixed in. Here the answer comes from the authenticated principal's role instead, so it
    /// holds whatever the render tree did.
    /// </para>
    /// <para>
    /// The qualifying roles are named rather than compared as ordinals (<c>role &gt;= Editor</c>), for
    /// the same reason <c>AuthPolicies</c> names them: an ordinal comparison silently widens every gate
    /// the day a role is added in the middle.
    /// </para>
    /// </summary>
    private static bool CanManage(UserRole role) => role is UserRole.Editor or UserRole.Admin;

    /// <summary>
    /// Refuses, and says so in the log. Callers get plain <c>false</c> — the same answer as "that
    /// incident is gone" — because a caller that reaches here is either a bug or someone probing, and
    /// neither is owed the distinction. The log is where the two are told apart, which is the same
    /// arrangement the login page uses.
    /// </summary>
    private bool Refuse(UserRole role, string action, long incidentId)
    {
        log.LogWarning(
            "Refused {Action} on incident {IncidentId}: role {Role} may not manage incidents. The UI does "
            + "not offer this action to that role, so reaching this check means either a defect or a "
            + "caller that bypassed the page.",
            action, incidentId, role);
        return false;
    }

    /// <summary>
    /// Attaches a newly-opened event to the incident it belongs to, opening a new one if no suitable
    /// incident is running. An uncorrelatable monitor always gets its own incident, so callers never
    /// have to special-case the single-monitor path.
    /// </summary>
    public async Task<Incident> AttachAsync(
        AppDbContext db, MonitorEvent ev, Monitor monitor, DateTime now, CancellationToken ct = default)
    {
        var key = await keys.GetKeyAsync(monitor.Type, monitor.ConfigJson, ct);

        var incident = key is null ? null : await FindOpenAsync(db, key, now, ct);

        if (incident is null)
        {
            incident = new Incident
            {
                CorrelationKey = key,
                Title = monitor.Name,
                StartedAt = now,
                LastEventAt = now,
                Severity = ev.ToStatus,
            };
            db.Incidents.Add(incident);
        }
        else
        {
            incident.LastEventAt = now;
            if (Rank(ev.ToStatus) > Rank(incident.Severity))
                incident.Severity = ev.ToStatus;
        }

        incident.Events.Add(ev);
        ev.Incident = incident;
        incident.MonitorCount = incident.Events.Select(e => e.MonitorId).Distinct().Count();
        return incident;
    }

    /// <summary>
    /// The open incident on this key that is still accepting members. Bounded by the correlation window
    /// measured from <see cref="Incident.LastEventAt"/>, not <see cref="Incident.StartedAt"/>: an incident
    /// that has been open for a week because one monitor never recovered must not silently absorb an
    /// unrelated failure on the same host today.
    /// <para>
    /// The whole incident is loaded with its events because the caller needs the tracked graph to decide
    /// closure without re-reading rows it has already modified in memory.
    /// </para>
    /// </summary>
    private async Task<Incident?> FindOpenAsync(AppDbContext db, string key, DateTime now, CancellationToken ct)
    {
        var cutoff = now - CorrelationWindow;
        return await db.Incidents
            .Include(i => i.Events)
            .Where(i => i.ResolvedAt == null && i.CorrelationKey == key && i.LastEventAt >= cutoff)
            .OrderByDescending(i => i.LastEventAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Loads an incident and its events into the tracker so closure can be judged in memory.</summary>
    public Task<Incident?> LoadAsync(AppDbContext db, long incidentId, CancellationToken ct = default)
        => db.Incidents.Include(i => i.Events).FirstOrDefaultAsync(i => i.Id == incidentId, ct);

    /// <summary>
    /// Closes the incident once every member event has resolved.
    /// <para>
    /// <b>Call this after any new event has been attached, never before.</b> An escalation
    /// (<c>EventAction.ResolveAndOpen</c>, e.g. Degraded → Down) resolves one event and opens another in
    /// the same beat; judged in between, every member would momentarily look resolved and one continuous
    /// outage would be recorded as two incidents.
    /// </para>
    /// </summary>
    public static void CloseIfAllResolved(Incident incident, DateTime now)
    {
        if (incident.ResolvedAt is not null) return;
        if (incident.Events.Any(e => e.ResolvedAt is null)) return;

        incident.ResolvedAt = now;
        incident.DurationSeconds = (long)Math.Max(0, (now - incident.StartedAt).TotalSeconds);
    }

    // --- Read side ---------------------------------------------------------------------------------
    //
    // Unlike the methods above, these own their own context: they serve the UI rather than participating
    // in the writer's single transaction.

    /// <summary>Open incidents, worst and newest first, with their member events and monitor names.</summary>
    public async Task<List<Incident>> GetOpenAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var open = await QueryWithMembers(db)
            .Where(i => i.ResolvedAt == null)
            .ToListAsync(ct);

        return [.. open.OrderByDescending(i => Rank(i.Severity)).ThenByDescending(i => i.StartedAt)];
    }

    /// <summary>The most recent incidents regardless of state, for the history view.</summary>
    public async Task<List<Incident>> GetRecentAsync(int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await QueryWithMembers(db)
            .OrderByDescending(i => i.StartedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<Incident?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await QueryWithMembers(db).FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    private static IQueryable<Incident> QueryWithMembers(AppDbContext db) =>
        db.Incidents.AsNoTracking()
            .Include(i => i.Events).ThenInclude(e => e.Monitor)
            .Include(i => i.Updates).ThenInclude(u => u.PostedBy)
            .Include(i => i.AcknowledgedBy);

    /// <summary>
    /// The open incident an alert for this monitor belongs to, with its members loaded.
    /// <para>
    /// Looked up by correlation key first and by membership second, and the order matters. When a host is
    /// already acknowledged and the twenty-first monitor on it now fails, that monitor has no incident
    /// membership yet — the alert is being evaluated concurrently with the writer that will attach it —
    /// but the incident on its key is already there. Membership then catches what the key cannot: a
    /// long-running incident whose last member joined before the correlation window closed, which is the
    /// normal case for the repeat-while-down alert.
    /// </para>
    /// <para>
    /// Lives here rather than in the alerting code because both the suppression gate and the alert
    /// enrichment need exactly this incident, and two copies of the fallback order would drift.
    /// </para>
    /// </summary>
    public async Task<Incident?> FindOpenForMonitorAsync(int monitorId, DateTime at, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var monitor = await db.Monitors.AsNoTracking().FirstOrDefaultAsync(m => m.Id == monitorId, ct);
        if (monitor is null) return null;

        var key = await keys.GetKeyAsync(monitor.Type, monitor.ConfigJson, ct);
        if (key is not null)
        {
            var cutoff = at - CorrelationWindow;
            var byKey = await OpenWithMembers(db)
                .Where(i => i.CorrelationKey == key && i.LastEventAt >= cutoff)
                .OrderByDescending(i => i.LastEventAt)
                .FirstOrDefaultAsync(ct);
            if (byKey is not null) return byKey;
        }

        return await OpenWithMembers(db)
            .Where(i => i.Events.Any(e => e.MonitorId == monitorId))
            .OrderByDescending(i => i.LastEventAt)
            .FirstOrDefaultAsync(ct);
    }

    private static IQueryable<Incident> OpenWithMembers(AppDbContext db) =>
        db.Incidents.AsNoTracking()
            .Include(i => i.Events).ThenInclude(e => e.Monitor)
            .Where(i => i.ResolvedAt == null);

    // --- Status pages ------------------------------------------------------------------------------

    /// <summary>
    /// Published incidents touching the monitors on a status page, projected down to what that page may
    /// say. Open incidents always appear; resolved ones stay up for <paramref name="resolvedFor"/> so a
    /// reader arriving after the fact still sees what happened.
    /// <para>
    /// Severity, timeline and affected-monitor list are all derived from the events belonging to this
    /// page's own monitors, never from the incident row — the incident spans every customer sharing the
    /// failing infrastructure. See <see cref="PublicIncident"/>.
    /// </para>
    /// </summary>
    public async Task<List<PublicIncident>> GetForStatusPageAsync(
        int statusPageId, DateTime now, TimeSpan resolvedFor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var pageMonitors = await db.StatusPageMonitors.AsNoTracking()
            .Where(spm => spm.StatusPageId == statusPageId)
            .Select(spm => spm.MonitorId)
            .ToListAsync(ct);
        if (pageMonitors.Count == 0) return [];

        var cutoff = now - resolvedFor;
        var incidents = await db.Incidents.AsNoTracking()
            .Include(i => i.Events).ThenInclude(e => e.Monitor)
            .Include(i => i.Updates)
            .Where(i => i.Published
                        && (i.ResolvedAt == null || i.ResolvedAt >= cutoff)
                        && i.Events.Any(e => pageMonitors.Contains(e.MonitorId)))
            .OrderByDescending(i => i.StartedAt)
            .ToListAsync(ct);

        var visible = pageMonitors.ToHashSet();
        var published = new List<PublicIncident>(incidents.Count);

        foreach (var i in incidents)
        {
            // Everything published below is derived from these member events, never from the incident
            // row. The incident's own Severity, StartedAt and ResolvedAt span every monitor that shared
            // the failing infrastructure — several customers, by design — so publishing them would head
            // this page with "Down since 01:00, ongoing" while the monitor row beneath it reads
            // Operational, and would quietly tell the reader how long somebody else's outage ran. The
            // names are intersected for exactly that reason; the timeline has to be as well.
            var members = i.Events.Where(e => visible.Contains(e.MonitorId)).ToList();

            // The query selects on the same predicate, so this cannot be empty — but the two would have
            // to change together, and an empty Min() below would throw on an anonymous page.
            if (members.Count == 0) continue;

            // Open while any of this page's own members is still open. Severity is the worst state they
            // reached and is not walked back as they recover, which is the rule Incident.Severity already
            // follows: a service that was fully down is not retold afterwards as merely slow.
            var resolvedAt = members.All(e => e.ResolvedAt is not null) ? members.Max(e => e.ResolvedAt) : null;
            var severity = members.Aggregate(
                MonitorStatus.Up, (worst, e) => Rank(e.ToStatus) > Rank(worst) ? e.ToStatus : worst);

            // The linger window has to be applied again to the page's own resolution, which can be far
            // older than the incident's. Otherwise an incident held open because another tenant never
            // recovered would pin a stale notice to this page indefinitely. This only ever narrows what
            // the database filter already returned: the incident's ResolvedAt is the max across all
            // members, so it is never earlier than this one.
            if (resolvedAt is { } r && r < cutoff) continue;

            published.Add(new PublicIncident(
                i.Id,
                severity,
                members.Min(e => e.StartedAt),
                resolvedAt,
                [.. members
                    .Select(e => e.Monitor?.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .Distinct()],
                [.. i.Updates
                    .OrderByDescending(u => u.PostedAt)
                    .Select(u => new PublicIncidentUpdate(u.Kind, u.Body, u.PostedAt))]));
        }

        // Ordered by what the page actually shows. The query ordered by the incident's own StartedAt,
        // which after the scoping above is no longer the date printed on the card.
        return [.. published.OrderByDescending(p => p.StartedAt)];
    }

    /// <summary>
    /// Posts an operator note against an incident. Returns false if the incident is gone, the body is
    /// blank, or <paramref name="actingRole"/> may not manage incidents.
    /// </summary>
    /// <param name="actingRole">
    /// The authenticated caller's role. No default, deliberately — every call site has to state who is
    /// acting, so "forgot to pass it" cannot quietly authorise the change. Same rule as
    /// <c>UserAccountService.CreateAsync</c>.
    /// </param>
    public async Task<bool> AddUpdateAsync(
        long incidentId, UserRole actingRole, IncidentUpdateKind kind, string body, int? userId, DateTime now,
        CancellationToken ct = default)
    {
        if (!CanManage(actingRole)) return Refuse(actingRole, "post an update", incidentId);
        if (string.IsNullOrWhiteSpace(body)) return false;

        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Incidents.AnyAsync(i => i.Id == incidentId, ct)) return false;

        db.IncidentUpdates.Add(new IncidentUpdate
        {
            IncidentId = incidentId,
            Kind = kind,
            Body = body.Trim(),
            PostedAt = now,
            PostedByUserId = userId,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Shows or hides an incident on status pages. Applies to resolved incidents too.
    /// <para>
    /// The one here with a reach beyond this instance: publishing puts an incident in front of whoever
    /// reads the customer-facing status page.
    /// </para>
    /// </summary>
    public async Task<bool> SetPublishedAsync(
        long incidentId, UserRole actingRole, bool published, CancellationToken ct = default)
    {
        if (!CanManage(actingRole)) return Refuse(actingRole, "publish or unpublish", incidentId);

        await using var db = await factory.CreateDbContextAsync(ct);
        var updated = await db.Incidents
            .Where(i => i.Id == incidentId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Published, published), ct);
        return updated > 0;
    }

    // --- Operator actions --------------------------------------------------------------------------

    /// <summary>
    /// Acknowledges an open incident, which stops the repeat-while-down alerts for every monitor in it
    /// until it resolves. Returns false if the incident is gone or already closed — acknowledging a
    /// finished outage would be a no-op that reads as success.
    /// </summary>
    public Task<bool> AcknowledgeAsync(long id, UserRole actingRole, int? userId, DateTime now, CancellationToken ct = default)
        => MutateOpenAsync(id, actingRole, "acknowledge", i =>
        {
            i.AcknowledgedAt = now;
            i.AcknowledgedByUserId = userId;
        }, ct);

    public Task<bool> UnacknowledgeAsync(long id, UserRole actingRole, CancellationToken ct = default)
        => MutateOpenAsync(id, actingRole, "un-acknowledge", i =>
        {
            i.AcknowledgedAt = null;
            i.AcknowledgedByUserId = null;
        }, ct);

    /// <summary>Silences repeat alerts for this incident until <paramref name="now"/> + <paramref name="duration"/>.</summary>
    public Task<bool> SnoozeAsync(long id, UserRole actingRole, TimeSpan duration, DateTime now, CancellationToken ct = default)
        => MutateOpenAsync(id, actingRole, "snooze", i => i.SnoozedUntil = now + duration, ct);

    public Task<bool> ClearSnoozeAsync(long id, UserRole actingRole, CancellationToken ct = default)
        => MutateOpenAsync(id, actingRole, "wake", i => i.SnoozedUntil = null, ct);

    /// <summary>
    /// The single funnel for the four operator actions above, which is why the role check sits here
    /// rather than being repeated four times — one of four is exactly the kind of omission that survives
    /// review.
    /// </summary>
    private async Task<bool> MutateOpenAsync(
        long id, UserRole actingRole, string action, Action<Incident> change, CancellationToken ct)
    {
        if (!CanManage(actingRole)) return Refuse(actingRole, action, id);

        await using var db = await factory.CreateDbContextAsync(ct);
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == id && i.ResolvedAt == null, ct);
        if (incident is null) return false;

        change(incident);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Severity ordering, which the <see cref="MonitorStatus"/> enum values do not give us — Down is 0 and
    /// Degraded is 3, so comparing the enum directly would rank an outage below a slowdown.
    /// </summary>
    private static int Rank(MonitorStatus s) => s switch
    {
        MonitorStatus.Down => 3,
        MonitorStatus.Degraded => 2,
        MonitorStatus.Pending => 1,
        _ => 0,
    };
}
