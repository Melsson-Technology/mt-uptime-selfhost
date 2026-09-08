using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Settings;

namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// Keeps the database from growing without bound on a micro instance. Once a day (and shortly
/// after startup) it:
/// <list type="number">
///   <item>rolls completed raw heartbeats up into hourly + daily <see cref="StatRollup"/> buckets
///         (so long-range uptime survives pruning),</item>
///   <item>prunes raw <see cref="Heartbeat"/> rows older than the retention window in batches
///         (short write locks, never one giant DELETE), and prunes stale hourly rollups,</item>
///   <item>prunes resolved <see cref="Domain.Incident"/>s past their own, much longer window, along
///         with any incident left with no members by a deleted monitor, then</item>
///   <item>on SQLite only, <c>wal_checkpoint(TRUNCATE)</c> + <c>incremental_vacuum</c> to actually
///         return freed pages to the OS (a full <c>VACUUM</c> is deliberately avoided — it rewrites
///         the whole file). Other engines reclaim their own space; see <see cref="RetentionDialect"/>.</item>
/// </list>
/// Rollup runs <em>before</em> prune so no completed bucket is lost. The manual
/// <see cref="RunCleanupAsync"/> entry point (used by the Settings page) shares a lock with the timer.
/// <para>
/// <b>This is the only part of the engine that writes SQL by hand, so it is the only part that can be
/// wrong on one database and right on another.</b> Everything that differs between engines lives in
/// <see cref="RetentionDialect"/> rather than inline here — four details, all of which were written for
/// SQLite alone and all of which failed the first time this ran on MySQL.
/// </para>
/// </summary>
public sealed class RetentionService(
    IDbContextFactory<AppDbContext> factory,
    ISettingsService settings,
    IOptions<EngineOptions> options,
    ILogger<RetentionService> log) : BackgroundService
{
    private const int DeleteBatchSize = 5000;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly EngineOptions _options = options.Value;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>When the last cleanup finished (UTC), for display on the Settings page.</summary>
    public DateTime? LastRunUtc { get; private set; }

    /// <summary>Human-readable summary of the last cleanup, for display on the Settings page.</summary>
    public string? LastRunSummary { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RunRetention)
        {
            // Said out loud, once, at Information. A database that is quietly never pruned looks exactly
            // like one that is being pruned correctly, right up until the disk fills.
            log.LogInformation(
                "Retention is disabled in this process (Engine:RunRetention=false); it must be run "
                + "centrally instead, or nothing will prune this database");
            return;
        }

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCleanupAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Retention cycle failed"); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Run one rollup + prune + vacuum cycle now. Serialized against the daily timer.</summary>
    public async Task<RetentionRunResult> RunCleanupAsync(CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            // Resolved once per run, from a context, because the provider cannot change under a running
            // process. Reading it costs no connection — EF knows the provider from the options.
            RetentionDialect dialect;
            await using (var probe = await factory.CreateDbContextAsync(ct))
                dialect = RetentionDialect.For(probe.Database.ProviderName);

            var rolled = await RollUpAsync(dialect, ct);
            var (rawDeleted, hourlyDeleted) = await PruneAsync(dialect, ct);
            var incidentsDeleted = await PruneIncidentsAsync(ct);
            if (dialect.CanReclaimFreePages && (rawDeleted > 0 || hourlyDeleted > 0 || incidentsDeleted > 0))
                await CheckpointAndVacuumAsync(ct);

            LastRunUtc = DateTime.UtcNow;
            LastRunSummary =
                $"{rolled} bucket(s) rolled up · {rawDeleted} heartbeat(s) pruned · "
                + $"{hourlyDeleted} hourly bucket(s) pruned · {incidentsDeleted} incident(s) pruned";
            log.LogInformation("Retention run complete: {Summary}", LastRunSummary);
            return new RetentionRunResult(rolled, rawDeleted, hourlyDeleted, incidentsDeleted);
        }
        finally { _runLock.Release(); }
    }

    // --- Rollup -------------------------------------------------------------------------------

    private async Task<int> RollUpAsync(RetentionDialect dialect, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var hourFloor = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var dayFloor = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);

        await using var db = await factory.CreateDbContextAsync(ct);
        var total = 0;
        // The bucket key is a truncated timestamp, and how you truncate one is the first thing that is
        // not portable — see RetentionDialect. Both forms yield exactly "yyyy-MM-dd HH:mm:ss".
        total += await RollUpPeriodAsync(db, dialect, RollupPeriod.Hourly, TimeSpan.FromHours(1), hourFloor, ct);
        total += await RollUpPeriodAsync(db, dialect, RollupPeriod.Daily, TimeSpan.FromDays(1), dayFloor, ct);
        return total;
    }

    private async Task<int> RollUpPeriodAsync(
        AppDbContext db, RetentionDialect dialect, RollupPeriod period, TimeSpan size, DateTime boundary,
        CancellationToken ct)
    {
        // Watermark = newest bucket already rolled up for this period; resume just after it.
        var watermark = await db.StatRollups
            .Where(r => r.Period == period)
            .MaxAsync(r => (DateTime?)r.BucketStart, ct);
        DateTime? from = watermark.HasValue ? watermark.Value + size : null;
        if (from.HasValue && from.Value >= boundary) return 0; // no newly-completed buckets

        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        var rows = new List<AggRow>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = BuildRollupSql(dialect, period, from.HasValue);
            AddParam(cmd, ParameterPrefix + "boundary", boundary);
            if (from.HasValue) AddParam(cmd, ParameterPrefix + "from", from.Value);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var bucket = DateTime.SpecifyKind(
                    DateTime.ParseExact(reader.GetString(1), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    DateTimeKind.Utc);
                // Read the aggregates through Convert rather than GetInt32/GetDouble. SQLite is
                // dynamically typed and hands back whatever fits; MySQL returns SUM(...) as DECIMAL,
                // and a strict GetInt32 on a decimal is an InvalidCastException. The columns are
                // aggregates of a CASE expression either way, so the CLR type is the driver's choice
                // and not something the schema pins down.
                rows.Add(new AggRow(
                    reader.GetInt32(0), bucket,
                    Count(reader, 2), Count(reader, 3), Count(reader, 4), Count(reader, 5),
                    Count(reader, 6),
                    Millis(reader, 7), Millis(reader, 8), Millis(reader, 9)));
            }
        }

        if (rows.Count == 0) return 0;

        foreach (var r in rows)
        {
            db.StatRollups.Add(new StatRollup
            {
                MonitorId = r.MonitorId,
                Period = period,
                BucketStart = r.BucketStart,
                UpCount = r.Up,
                DownCount = r.Down,
                PendingCount = r.Pending,
                DegradedCount = r.Degraded,
                MaintenanceCount = r.Maintenance,
                PingAvgMs = r.Avg,
                PingMinMs = r.Min,
                PingMaxMs = r.Max,
            });
        }
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    // --- Prune --------------------------------------------------------------------------------

    private async Task<(long raw, long hourly)> PruneAsync(RetentionDialect dialect, CancellationToken ct)
    {
        var retention = await settings.GetRetentionAsync(ct);
        var rawCutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retention.RawDays));

        // Delete raw heartbeats in bounded batches so each write lock is short. How the batch is bounded
        // is the second thing that is not portable: SQLite goes through a subquery because DELETE...LIMIT
        // is an optional compile-time flag there, and MySQL rejects LIMIT inside an IN subquery outright.
        // See RetentionDialect.
        var deleteSql = dialect.BatchDeleteSql(DeleteBatchSize);

        long rawDeleted = 0;
        while (true)
        {
            int n;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                n = await db.Database.ExecuteSqlRawAsync(deleteSql, new object[] { rawCutoff }, ct);
            }
            rawDeleted += n;
            if (n < DeleteBatchSize) break;
            ct.ThrowIfCancellationRequested();
        }

        // Hourly rollups age out too (daily rollups are kept indefinitely — one tiny row per day).
        var hourlyCutoff = DateTime.UtcNow.AddDays(-_options.HourlyRetentionDays);
        long hourlyDeleted;
        await using (var db2 = await factory.CreateDbContextAsync(ct))
        {
            hourlyDeleted = await db2.StatRollups
                .Where(r => r.Period == RollupPeriod.Hourly && r.BucketStart < hourlyCutoff)
                .ExecuteDeleteAsync(ct);
        }

        return (rawDeleted, hourlyDeleted);
    }

    /// <summary>
    /// Prunes incidents, which nothing did until now — <see cref="Domain.Incident"/> and
    /// <see cref="Domain.IncidentUpdate"/> grew for the life of the install. Two separate jobs:
    /// <list type="number">
    ///   <item><b>Resolved incidents past the window.</b> Open ones are never touched at any age.</item>
    ///   <item><b>Incidents with no members left.</b> Deleting a monitor cascades its
    ///         <see cref="Domain.MonitorEvent"/>s, which can empty an incident of everything it grouped
    ///         — including an open one, which then sits on <c>/incidents</c> as a permanent entry with
    ///         nothing under it and no way to clear it.</item>
    /// </list>
    /// <para>
    /// The member-less sweep carries an hour's grace it does not strictly need. A committed incident with
    /// no events should be impossible otherwise — <c>AttachAsync</c> adds the incident and its first event
    /// in the same <c>SaveChangesAsync</c>, so no other connection ever observes the intermediate state —
    /// but the cost of that reasoning being wrong is deleting a live incident during an outage, and the
    /// cost of the grace is that a ghost row lingers for an hour.
    /// </para>
    /// </summary>
    private async Task<long> PruneIncidentsAsync(CancellationToken ct)
    {
        var resolvedCutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _options.IncidentRetentionDays));
        var orphanCutoff = DateTime.UtcNow.AddHours(-1);

        await using var db = await factory.CreateDbContextAsync(ct);

        var aged = await db.Incidents
            .Where(i => i.ResolvedAt != null && i.ResolvedAt < resolvedCutoff)
            .ExecuteDeleteAsync(ct);

        var orphaned = await db.Incidents
            .Where(i => !i.Events.Any() && i.StartedAt < orphanCutoff)
            .ExecuteDeleteAsync(ct);

        return aged + orphaned;
    }

    // --- Reclaim disk -------------------------------------------------------------------------

    /// <summary>
    /// SQLite only — the caller checks <see cref="RetentionDialect.CanReclaimFreePages"/> first. On any
    /// other engine this is both invalid syntax and unnecessary work.
    /// </summary>
    private async Task CheckpointAndVacuumAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Fold the WAL back into the main file and truncate it, then return freelist pages to the OS
        // (works because the database was created with auto_vacuum=INCREMENTAL).
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA incremental_vacuum;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// <c>@</c>, and not SQLite's <c>$</c>. Microsoft.Data.Sqlite accepts <c>@</c>, <c>$</c> and <c>:</c>
    /// alike; MySQL accepts only <c>@</c>. So this prefix is the one that works everywhere — and the
    /// <c>$</c> it replaced is what produced <i>"Unknown column '$boundary' in 'where clause'"</i> the
    /// first time a retention cycle ran against MySQL.
    /// </summary>
    private const string ParameterPrefix = "@";

    /// <summary>
    /// The rollup query for one period.
    /// <para>
    /// Built by a named method rather than inline so it can be asserted against without a database:
    /// the ways this can be wrong are provider-specific and silent, and the SQLite suite cannot see
    /// them. <c>internal</c> for that reason alone — see the test assembly named in the csproj.
    /// </para>
    /// <para>
    /// Maintenance beats are counted only into <c>MaintC</c> and are kept out of the four status buckets
    /// and the response-time aggregates. That is what makes <c>StatRollup.Total</c> the uptime
    /// denominator directly, with no later subtraction — see <c>StatRollup.MaintenanceCount</c> for why
    /// subtracting afterwards would double-count.
    /// </para>
    /// </summary>
    internal static string BuildRollupSql(RetentionDialect dialect, RollupPeriod period, bool hasFrom)
    {
        var bucketExpr = dialect.BucketFor(period);

        return $"""
            SELECT MonitorId,
                   {bucketExpr} AS Bucket,
                   SUM(CASE WHEN Maintenance = 0 AND Status = 1 THEN 1 ELSE 0 END) AS UpC,
                   SUM(CASE WHEN Maintenance = 0 AND Status = 0 THEN 1 ELSE 0 END) AS DownC,
                   SUM(CASE WHEN Maintenance = 0 AND Status = 2 THEN 1 ELSE 0 END) AS PendC,
                   SUM(CASE WHEN Maintenance = 0 AND Status = 3 THEN 1 ELSE 0 END) AS DegC,
                   SUM(CASE WHEN Maintenance = 1 THEN 1 ELSE 0 END) AS MaintC,
                   AVG(CASE WHEN Maintenance = 0 THEN ResponseTimeMs END) AS AvgMs,
                   MIN(CASE WHEN Maintenance = 0 THEN ResponseTimeMs END) AS MinMs,
                   MAX(CASE WHEN Maintenance = 0 THEN ResponseTimeMs END) AS MaxMs
            FROM Heartbeats
            WHERE Timestamp < {ParameterPrefix}boundary{(hasFrom ? $" AND Timestamp >= {ParameterPrefix}from" : "")}
            GROUP BY MonitorId, {bucketExpr};
            """;
    }

    /// <summary>A <c>SUM(CASE ...)</c> column, whatever numeric type the driver chose to return it as.</summary>
    private static int Count(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>An AVG/MIN/MAX over the nullable response time. Null when every beat in the bucket was null.</summary>
    private static double? Millis(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private readonly record struct AggRow(
        int MonitorId, DateTime BucketStart, int Up, int Down, int Pending, int Degraded, int Maintenance,
        double? Avg, double? Min, double? Max);
}

/// <summary>Outcome of one <see cref="RetentionService.RunCleanupAsync"/> cycle.</summary>
public sealed record RetentionRunResult(
    int BucketsRolledUp, long RawHeartbeatsPruned, long HourlyBucketsPruned, long IncidentsPruned);
