using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Data;

namespace MT.Uptime.Core.Maintenance;

/// <summary>
/// Decides whether a monitor is inside a maintenance window, and manages the windows themselves.
/// <para>
/// The lookup sits on the heartbeat writer's path — it is consulted for every beat, not just for
/// transitions — so it answers from a cached snapshot refreshed on a short timer and invalidated on any
/// edit. Windows are few and change rarely; heartbeats are constant.
/// </para>
/// </summary>
public sealed class MaintenanceWindowService(IDbContextFactory<AppDbContext> factory)
{
    /// <summary>
    /// How stale the snapshot may get. Edits invalidate it immediately, so this only bounds changes made
    /// out of band (another process, a restored database) — a window opening up to half a minute late is
    /// not worth a query per heartbeat.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Snapshot? _snapshot;

    private sealed record Scope(MaintenanceWindow Window, HashSet<int> MonitorIds, HashSet<int> TagIds);

    private sealed record Snapshot(DateTime LoadedAt, List<Scope> Scopes, Dictionary<int, HashSet<int>> TagsByMonitor);

    // --- The question the writer and the dispatcher both ask ----------------------------------------

    /// <summary>The window covering this monitor at <paramref name="utc"/>, or null if none is open.</summary>
    public async Task<MaintenanceWindow?> ActiveForAsync(int monitorId, DateTime utc, CancellationToken ct = default)
    {
        var snap = await GetSnapshotAsync(ct);

        foreach (var scope in snap.Scopes)
        {
            if (!Covers(scope, monitorId, snap)) continue;
            if (IsOpenAt(scope.Window, utc)) return scope.Window;
        }

        return null;
    }

    public async Task<bool> IsInMaintenanceAsync(int monitorId, DateTime utc, CancellationToken ct = default)
        => await ActiveForAsync(monitorId, utc, ct) is not null;

    private static bool Covers(Scope scope, int monitorId, Snapshot snap)
    {
        if (scope.Window.AppliesToAllMonitors) return true;
        if (scope.MonitorIds.Contains(monitorId)) return true;
        if (scope.TagIds.Count == 0) return false;

        return snap.TagsByMonitor.TryGetValue(monitorId, out var tags) && tags.Overlaps(scope.TagIds);
    }

    /// <summary>
    /// Whether the window is open at an instant.
    /// <para>
    /// Recurring windows are evaluated in their own zone rather than UTC, because "Sundays at 02:00" is a
    /// wall-clock statement — evaluated in UTC it would drift by an hour twice a year. The previous local
    /// day is checked as well as the current one so a window that runs past midnight is still open on the
    /// far side of it.
    /// </para>
    /// <para>
    /// On a spring-forward the nominal start may be a local time that does not exist; the window then
    /// effectively begins at the next real instant, which is the sane reading of "start at 02:00" on a day
    /// with no 02:00.
    /// </para>
    /// </summary>
    public static bool IsOpenAt(MaintenanceWindow w, DateTime utc)
    {
        if (!w.Enabled) return false;

        if (w.Recurrence == MaintenanceRecurrence.Once)
            return w.StartsAt is { } start && w.EndsAt is { } end && utc >= start && utc < end;

        if (w.DaysOfWeekMask == 0 || w.DurationMinutes <= 0) return false;

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, ResolveZone(w.TimeZoneId));

        for (var offset = 0; offset >= -1; offset--)
        {
            var day = local.Date.AddDays(offset);
            if ((w.DaysOfWeekMask & (1 << (int)day.DayOfWeek)) == 0) continue;

            var start = day.AddMinutes(w.StartMinuteOfDay);
            if (local >= start && local < start.AddMinutes(w.DurationMinutes)) return true;
        }

        return false;
    }

    /// <summary>
    /// The window's zone, falling back to UTC when the host does not know the id. A window that cannot be
    /// placed on a clock must not throw on the heartbeat path — running it in UTC is wrong by an offset,
    /// but throwing here would stop the writer for every monitor.
    /// </summary>
    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    // --- Snapshot ----------------------------------------------------------------------------------

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var current = _snapshot;
        if (current is not null && DateTime.UtcNow - current.LoadedAt < CacheTtl) return current;

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have refreshed while we waited for the lock.
            current = _snapshot;
            if (current is not null && DateTime.UtcNow - current.LoadedAt < CacheTtl) return current;

            await using var db = await factory.CreateDbContextAsync(ct);
            var windows = await db.MaintenanceWindows.AsNoTracking()
                .Include(w => w.Monitors)
                .Include(w => w.Tags)
                .Where(w => w.Enabled)
                .ToListAsync(ct);

            var scopes = windows
                .Select(w => new Scope(w,
                    [.. w.Monitors.Select(m => m.MonitorId)],
                    [.. w.Tags.Select(t => t.TagId)]))
                .ToList();

            // Only loaded when some window is actually scoped by tag, so the common case costs nothing.
            var tagsByMonitor = scopes.Any(s => s.TagIds.Count > 0)
                ? (await db.MonitorTags.AsNoTracking().ToListAsync(ct))
                    .GroupBy(mt => mt.MonitorId)
                    .ToDictionary(g => g.Key, g => g.Select(mt => mt.TagId).ToHashSet())
                : [];

            var fresh = new Snapshot(DateTime.UtcNow, scopes, tagsByMonitor);
            _snapshot = fresh;
            return fresh;
        }
        finally { _refreshLock.Release(); }
    }

    /// <summary>Drops the cached snapshot so the next lookup reloads. Called after any edit.</summary>
    public void Invalidate() => _snapshot = null;

    // --- Announcements -----------------------------------------------------------------------------

    /// <summary>
    /// Windows covering any of <paramref name="monitorIds"/> that are open now or due to open within
    /// <paramref name="horizon"/>, for announcing on a status page. Only windows marked
    /// <see cref="MaintenanceWindow.Publish"/> are returned.
    /// </summary>
    public async Task<List<(MaintenanceWindow Window, DateTime StartsAt, DateTime EndsAt, bool InProgress)>>
        UpcomingForAsync(
            IReadOnlyCollection<int> monitorIds, DateTime fromUtc, TimeSpan horizon, CancellationToken ct = default)
    {
        if (monitorIds.Count == 0) return [];

        var snap = await GetSnapshotAsync(ct);
        var results = new List<(MaintenanceWindow, DateTime, DateTime, bool)>();

        foreach (var scope in snap.Scopes)
        {
            if (!scope.Window.Publish) continue;
            if (!monitorIds.Any(id => Covers(scope, id, snap))) continue;

            var occurrence = CurrentOrNextOccurrence(scope.Window, fromUtc, horizon);
            if (occurrence is not { } o) continue;

            results.Add((scope.Window, o.Start, o.End, o.Start <= fromUtc));
        }

        return [.. results.OrderBy(r => r.Item2)];
    }

    /// <summary>
    /// The occurrence in progress at <paramref name="fromUtc"/>, or the next one starting within
    /// <paramref name="horizon"/>; null if neither.
    /// </summary>
    public static (DateTime Start, DateTime End)? CurrentOrNextOccurrence(
        MaintenanceWindow w, DateTime fromUtc, TimeSpan horizon)
    {
        if (!w.Enabled) return null;

        if (w.Recurrence == MaintenanceRecurrence.Once)
        {
            if (w.StartsAt is not { } s || w.EndsAt is not { } e) return null;
            if (e <= fromUtc) return null;                        // already over
            return s <= fromUtc + horizon ? (s, e) : null;
        }

        if (w.DaysOfWeekMask == 0 || w.DurationMinutes <= 0) return null;

        var tz = ResolveZone(w.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, tz);
        var duration = TimeSpan.FromMinutes(w.DurationMinutes);

        // Start one day back so an occurrence that began yesterday and runs past midnight is still found.
        for (var offset = -1; offset <= horizon.TotalDays + 1; offset++)
        {
            var day = localNow.Date.AddDays(offset);
            if ((w.DaysOfWeekMask & (1 << (int)day.DayOfWeek)) == 0) continue;

            var startUtc = ToUtc(day.AddMinutes(w.StartMinuteOfDay), tz);
            var endUtc = startUtc + duration;

            if (endUtc <= fromUtc) continue;                       // that one is done
            return startUtc <= fromUtc + horizon ? (startUtc, endUtc) : null;
        }

        return null;
    }

    /// <summary>
    /// Converts a local wall-clock time to UTC, stepping over the gap on a spring-forward. A window
    /// nominally starting at a local time that does not exist that day begins at the first instant that
    /// does, which is the sane reading of "start at 02:00" on a day with no 02:00.
    /// </summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(unspecified))
            unspecified = unspecified.AddMinutes(15);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }

    // --- Management --------------------------------------------------------------------------------

    public async Task<List<MaintenanceWindow>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MaintenanceWindows.AsNoTracking()
            .Include(w => w.Monitors).ThenInclude(m => m.Monitor)
            .Include(w => w.Tags).ThenInclude(t => t.Tag)
            .OrderBy(w => w.Name)
            .ToListAsync(ct);
    }

    public async Task<MaintenanceWindow?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.MaintenanceWindows.AsNoTracking()
            .Include(w => w.Monitors)
            .Include(w => w.Tags)
            .FirstOrDefaultAsync(w => w.Id == id, ct);
    }

    /// <summary>
    /// Creates or updates a window and replaces its scope wholesale. Returns an error string, or null on
    /// success — matching how <c>TagService</c> reports validation failures to the editor pages.
    /// </summary>
    public async Task<string?> SaveAsync(
        MaintenanceWindow window, IEnumerable<int> monitorIds, IEnumerable<int> tagIds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(window.Name)) return "Give the window a name.";

        if (window.Recurrence == MaintenanceRecurrence.Once)
        {
            if (window.StartsAt is null || window.EndsAt is null) return "Set a start and an end.";
            if (window.EndsAt <= window.StartsAt) return "The end must be after the start.";
        }
        else
        {
            if (window.DaysOfWeekMask == 0) return "Choose at least one day.";
            if (window.DurationMinutes <= 0) return "Set a duration.";
        }

        // Materialized once: the callers pass LINQ projections over UI state, and these are enumerated
        // several times below.
        var monitors = monitorIds.Distinct().ToArray();
        var tags = tagIds.Distinct().ToArray();

        if (!window.AppliesToAllMonitors && monitors.Length == 0 && tags.Length == 0)
            return "Choose the monitors or tags this covers, or apply it to everything.";

        await using var db = await factory.CreateDbContextAsync(ct);

        MaintenanceWindow row;
        if (window.Id == 0)
        {
            row = new MaintenanceWindow();
            db.MaintenanceWindows.Add(row);
        }
        else
        {
            var existing = await db.MaintenanceWindows
                .Include(w => w.Monitors)
                .Include(w => w.Tags)
                .FirstOrDefaultAsync(w => w.Id == window.Id, ct);
            if (existing is null) return "That window no longer exists.";

            // Scope is replaced wholesale rather than diffed — the editor always posts the full set.
            existing.Monitors.Clear();
            existing.Tags.Clear();
            row = existing;
        }

        row.Name = window.Name.Trim();
        row.Description = window.Description;
        row.Enabled = window.Enabled;
        row.Recurrence = window.Recurrence;
        row.StartsAt = window.StartsAt;
        row.EndsAt = window.EndsAt;
        row.StartMinuteOfDay = window.StartMinuteOfDay;
        row.DurationMinutes = window.DurationMinutes;
        row.DaysOfWeekMask = window.DaysOfWeekMask;
        row.TimeZoneId = window.TimeZoneId;
        row.AppliesToAllMonitors = window.AppliesToAllMonitors;
        row.Publish = window.Publish;

        foreach (var id in monitors)
            row.Monitors.Add(new MaintenanceWindowMonitor { MonitorId = id });
        foreach (var id in tags)
            row.Tags.Add(new MaintenanceWindowTag { TagId = id });

        await db.SaveChangesAsync(ct);
        Invalidate();
        return null;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.MaintenanceWindows.Where(w => w.Id == id).ExecuteDeleteAsync(ct);
        Invalidate();
    }
}
