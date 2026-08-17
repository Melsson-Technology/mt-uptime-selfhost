using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MT.Uptime.Core.Data;

/// <summary>
/// One-time database bootstrap, run once at application startup:
/// sets the persistent PRAGMAs that must exist <em>before</em> any tables are created
/// (WAL journal + incremental auto-vacuum), then applies EF Core migrations.
/// </summary>
public sealed class DatabaseInitializer(IDbContextFactory<AppDbContext> factory)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // auto_vacuum only takes effect if set on an empty database (before the first table exists),
        // so this must run before MigrateAsync creates the schema. Opening the connection here also
        // fires SqlitePragmaInterceptor, which puts the database into WAL mode.
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA auto_vacuum=INCREMENTAL;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await db.Database.MigrateAsync(ct);
    }
}
