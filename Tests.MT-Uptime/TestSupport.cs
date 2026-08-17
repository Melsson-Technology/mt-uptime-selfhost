using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Settings;

namespace MT.Uptime.Tests;

/// <summary>
/// A throwaway file-backed SQLite database wired exactly like production (WAL + incremental
/// auto-vacuum via <see cref="DatabaseInitializer"/> and the pragma interceptor), doubling as the
/// <see cref="IDbContextFactory{AppDbContext}"/> the services expect.
/// </summary>
sealed class TestDatabase : IDbContextFactory<AppDbContext>, IAsyncDisposable
{
    private readonly string _path;
    private readonly DbContextOptions<AppDbContext> _options;

    private TestDatabase(string path, DbContextOptions<AppDbContext> options)
    {
        _path = path;
        _options = options;
    }

    public AppDbContext CreateDbContext() => new(_options);

    public static async Task<TestDatabase> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mt-uptime-test-{Guid.NewGuid():N}.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(cs)
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;

        var db = new TestDatabase(path, options);
        await new DatabaseInitializer(db).InitializeAsync();
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.CompletedTask;
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best-effort temp cleanup */ }
    }

    // --- Seeding helpers ------------------------------------------------------------------------

    public async Task<int> SeedMonitorAsync()
    {
        await using var db = CreateDbContext();
        var m = new Monitor { Name = "test", Type = MonitorType.Http, ConfigJson = "{}" };
        db.Monitors.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    /// <summary>Seeds a monitor whose type and config matter — used by the incident-correlation tests.</summary>
    public async Task<int> SeedMonitorAsync(string name, MonitorType type = MonitorType.Http, string configJson = "{}")
    {
        await using var db = CreateDbContext();
        var m = new Monitor { Name = name, Type = type, ConfigJson = configJson };
        db.Monitors.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    public async Task AddBeatsAsync(
        int monitorId, DateTime start, MonitorStatus status, int count, double? ms = 100, bool maintenance = false)
    {
        await using var db = CreateDbContext();
        for (var i = 0; i < count; i++)
            db.Heartbeats.Add(new Heartbeat
            {
                MonitorId = monitorId,
                Timestamp = start.AddSeconds(i),
                Status = status,
                ResponseTimeMs = status == MonitorStatus.Up ? ms : null,
                Maintenance = maintenance,
            });
        await db.SaveChangesAsync();
    }

    public RetentionService NewRetention(int rawDays) => new(
        this,
        new FakeSettings(rawDays),
        Options.Create(new EngineOptions { RawRetentionDays = rawDays, HourlyRetentionDays = 180 }),
        NullLogger<RetentionService>.Instance);
}

/// <summary>Minimal <see cref="ISettingsService"/> that just reports a fixed raw-retention window.</summary>
sealed class FakeSettings(int rawDays) : ISettingsService
{
    public Task<EmailSettings> GetEmailAsync(CancellationToken ct = default) => Task.FromResult(new EmailSettings());
    public Task SaveEmailAsync(EmailSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    public Task<RetentionSettings> GetRetentionAsync(CancellationToken ct = default)
        => Task.FromResult(new RetentionSettings { RawDays = rawDays });
    public Task SaveRetentionAsync(RetentionSettings settings, CancellationToken ct = default) => Task.CompletedTask;
}
