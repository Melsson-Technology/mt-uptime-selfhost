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
/// Keeps the SQLite file from growing without bound on a micro instance. Once a day (and shortly
/// after startup) it:
/// <list type="number">
///   <item>rolls completed raw heartbeats up into hourly + daily <see cref="StatRollup"/> buckets
///         (so long-range uptime survives pruning),</item>
///   <item>prunes raw <see cref="Heartbeat"/> rows older than the retention window in batches
///         (short write locks, never one giant DELETE), and prunes stale hourly rollups, then</item>
///   <item><c>wal_checkpoint(TRUNCATE)</c> + <c>incremental_vacuum</c> to actually return freed pages
///         to the OS (a full <c>VACUUM</c> is deliberately avoided — it rewrites the whole file).</item>
/// </list>
/// Rollup runs <em>before</em> prune so no completed bucket is lost. The manual
/// <see cref="RunCleanupAsync"/> entry point (used by the Settings page) shares a lock with the timer.
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
            var rolled = await RollUpAsync(ct);
            var (rawDeleted, hourlyDeleted) = await PruneAsync(ct);
            if (rawDeleted > 0 || hourlyDeleted > 0)
                await CheckpointAndVacuumAsync(ct);

            LastRunUtc = DateTime.UtcNow;
            LastRunSummary =
                $"{rolled} bucket(s) rolled up · {rawDeleted} heartbeat(s) pruned · {hourlyDeleted} hourly bucket(s) pruned";
            log.LogInformation("Retention run complete: {Summary}", LastRunSummary);
            return new RetentionRunResult(rolled, rawDeleted, hourlyDeleted);
        }
        finally { _runLock.Release(); }
    }

    // --- Rollup -------------------------------------------------------------------------------

    private async Task<int> RollUpAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var hourFloor = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var dayFloor = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);

        await using var db = await factory.CreateDbContextAsync(ct);
        var total = 0;
        // Bucket key by string-slicing the fixed-width stored timestamp ("yyyy-MM-dd HH:mm:ss[.fff]"),
        // which is robust regardless of whether fractional seconds are present.
        total += await RollUpPeriodAsync(db, RollupPeriod.Hourly,
            "substr(Timestamp, 1, 13) || ':00:00'", TimeSpan.FromHours(1), hourFloor, ct);
        total += await RollUpPeriodAsync(db, RollupPeriod.Daily,
            "substr(Timestamp, 1, 10) || ' 00:00:00'", TimeSpan.FromDays(1), dayFloor, ct);
        return total;
    }

    private async Task<int> RollUpPeriodAsync(
        AppDbContext db, RollupPeriod period, string bucketExpr, TimeSpan size, DateTime boundary, CancellationToken ct)
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
            // Maintenance beats are counted only into MaintC and are kept out of the four status buckets
            // and the response-time aggregates. That is what makes StatRollup.Total the uptime
            // denominator directly, with no later subtraction — see StatRollup.MaintenanceCount for why
            // subtracting afterwards would double-count.
            cmd.CommandText = $"""
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
                WHERE Timestamp < $boundary{(from.HasValue ? " AND Timestamp >= $from" : "")}
                GROUP BY MonitorId, {bucketExpr};
                """;
            AddParam(cmd, "$boundary", boundary);
            if (from.HasValue) AddParam(cmd, "$from", from.Value);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var bucket = DateTime.SpecifyKind(
                    DateTime.ParseExact(reader.GetString(1), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    DateTimeKind.Utc);
                rows.Add(new AggRow(
                    reader.GetInt32(0), bucket,
                    reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    reader.IsDBNull(9) ? null : reader.GetDouble(9)));
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

    private async Task<(long raw, long hourly)> PruneAsync(CancellationToken ct)
    {
        var retention = await settings.GetRetentionAsync(ct);
        var rawCutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retention.RawDays));

        // Delete raw heartbeats in bounded batches so each write lock is short. The subquery+LIMIT form
        // works without SQLite's optional DELETE...LIMIT compile-time flag.
        long rawDeleted = 0;
        while (true)
        {
            int n;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                n = await db.Database.ExecuteSqlAsync(
                    $"DELETE FROM Heartbeats WHERE Id IN (SELECT Id FROM Heartbeats WHERE Timestamp < {rawCutoff} LIMIT {DeleteBatchSize})",
                    ct);
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

    // --- Reclaim disk -------------------------------------------------------------------------

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
public sealed record RetentionRunResult(int BucketsRolledUp, long RawHeartbeatsPruned, long HourlyBucketsPruned);
