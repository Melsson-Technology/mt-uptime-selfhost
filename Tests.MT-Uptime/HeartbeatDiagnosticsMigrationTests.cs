using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests;

/// <summary>
/// The AddHeartbeatDiagnostics migration, applied to a database that already has history in it.
/// <para>
/// Every other test creates its database at the latest migration, where the column already exists — so
/// none of them can tell whether the <em>upgrade</em> works. <c>Heartbeats</c> is the largest table on a
/// live install by a wide margin, and it is the one an operator would least like a migration to rewrite.
/// </para>
/// </summary>
public class HeartbeatDiagnosticsMigrationTests
{
    /// <summary>The migration immediately before this one — the state a live install upgrades from.</summary>
    private const string PreviousMigration = "20260818220716_AddUniqueUserEmail";

    [Fact]
    public async Task Upgrading_an_install_with_history_keeps_every_beat_and_leaves_them_null()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mt-uptime-diag-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString())
            .Options;

        try
        {
            await using (var db = new AppDbContext(options))
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            // Raw SQL, not the entity model: Heartbeat now has a Diagnostics property and the table at
            // this point has no such column.
            await using (var db = new AppDbContext(options))
            {
                // Braces doubled: ExecuteSqlRaw runs the string through composite formatting first, so a
                // JSON literal's { and } would be read as parameter placeholders.
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO Monitors (Name, Type, ConfigJson, IntervalSeconds, TimeoutSeconds, Enabled,
                                          CurrentStatus, RetryCount, ResendEveryN, UpsideDown,
                                          DegradedAfterChecks, CreatedAt, UpdatedAt)
                    VALUES ('web', 0, '{{"Url":"https://web.example.com/"}}', 60, 30, 1,
                            1, 3, 0, 0, 3, '2026-01-01 00:00:00', '2026-01-01 00:00:00');

                    INSERT INTO Heartbeats (MonitorId, Timestamp, Status, ResponseTimeMs, StatusCode, Message, Important, Attempt, Maintenance)
                    VALUES (1, '2026-09-10 00:24:42', 1, 46, '200', NULL, 0, 0, 0),
                           (1, '2026-09-10 00:25:42', 2, 21556, '521', 'Unexpected status 521', 1, 0, 0);
                    """);
            }

            // Upgrade.
            await using (var db = new AppDbContext(options))
                await db.GetService<IMigrator>().MigrateAsync();

            await using (var db = new AppDbContext(options))
            {
                var beats = await db.Heartbeats.AsNoTracking().OrderBy(h => h.Timestamp).ToListAsync();

                // Nothing lost, nothing rewritten.
                Assert.Equal(2, beats.Count);
                Assert.Equal("200", beats[0].StatusCode);
                Assert.Equal("521", beats[1].StatusCode);
                Assert.Equal(21556, beats[1].ResponseTimeMs);

                // Pre-existing history has no diagnostics, and must read as absent rather than blow up
                // the page that renders it.
                Assert.All(beats, b => Assert.Null(b.Diagnostics));
                Assert.All(beats, b => Assert.Null(CheckDiagnostics.FromJson(b.Diagnostics)));

                // And the new column is writable on the upgraded schema.
                beats[1].Diagnostics = new CheckDiagnostics
                {
                    Timings = new ProbeTimingBreakdown(12, 21400, null, 21550, 21556),
                    Headers = new Dictionary<string, string> { ["cf-ray"] = "9c1a-LHR" },
                }.ToJson();

                db.Heartbeats.Attach(beats[1]).State = EntityState.Modified;
                await db.SaveChangesAsync();
            }

            await using (var db = new AppDbContext(options))
            {
                var stored = await db.Heartbeats.AsNoTracking().OrderBy(h => h.Timestamp).LastAsync();
                var decoded = CheckDiagnostics.FromJson(stored.Diagnostics);

                Assert.NotNull(decoded);
                Assert.Equal("9c1a-LHR", decoded!.Headers["cf-ray"]);
                Assert.Equal(21400, decoded.Timings!.ConnectMs);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
