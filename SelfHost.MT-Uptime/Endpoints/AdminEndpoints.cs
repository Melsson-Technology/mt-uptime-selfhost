using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MT.Uptime.Core.Data;
using MT.Uptime.Web.Security;

namespace MT.Uptime.Web.Endpoints;

/// <summary>
/// Admin utilities: download a consistent backup of the SQLite database, and export the monitor
/// definitions as JSON. Both are GET downloads (no antiforgery needed).
/// <para>
/// <b>Admin, not merely authenticated.</b> The backup is the whole database — every account hash and
/// every encrypted secret — so anyone who can fetch it holds the instance offline. The export redacts
/// secrets, but still lists the internal hosts being monitored. Neither is something a Viewer should
/// be able to walk off with.
/// </para>
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app, string databasePath)
    {
        // Consistent snapshot via the SQLite online-backup API (safe even while the app is writing).
        // We checkpoint the WAL first so the copy is compact and fully current.
        app.MapGet("/admin/backup", async (IDbContextFactory<AppDbContext> factory, CancellationToken ct) =>
        {
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                var conn = db.Database.GetDbConnection();
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var tmp = Path.Combine(Path.GetTempPath(), $"mt-uptime-backup-{stamp}-{Guid.NewGuid():N}.db");

            // Pooling=false so disposing these connections actually releases the OS file handles —
            // a pooled connection would keep the temp file locked when we stream it below.
            var srcCs = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
            var dstCs = new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString();
            await using (var src = new SqliteConnection(srcCs))
            await using (var dst = new SqliteConnection(dstCs))
            {
                await src.OpenAsync(ct);
                await dst.OpenAsync(ct);
                src.BackupDatabase(dst);
            }

            // DeleteOnClose removes the temp file once the response has finished streaming.
            var stream = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
            return Results.File(stream, "application/octet-stream", $"mt-uptime-backup-{stamp}.db");
        }).RequireAuthorization(AuthPolicies.Admin);

        // Monitor inventory as JSON. Encrypted secrets inside ConfigJson (DB passwords) are redacted.
        app.MapGet("/admin/export/monitors", async (IDbContextFactory<AppDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var monitors = await db.Monitors.AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);

            var export = monitors.Select(m => new
            {
                m.Id,
                m.Name,
                Type = m.Type.ToString(),
                m.IntervalSeconds,
                m.TimeoutSeconds,
                m.RetryCount,
                m.ResendEveryN,
                m.UpsideDown,
                m.Enabled,
                Config = Redact(m.ConfigJson),
            }).ToList();

            var bytes = JsonSerializer.SerializeToUtf8Bytes(export, new JsonSerializerOptions { WriteIndented = true });
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            return Results.File(bytes, "application/json", $"mt-uptime-monitors-{stamp}.json");
        }).RequireAuthorization(AuthPolicies.Admin);
    }

    /// <summary>Parse a monitor's ConfigJson and null out any secret field before it leaves the box.</summary>
    private static JsonNode? Redact(string configJson)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(configJson); }
        catch { return null; }

        if (node is JsonObject obj && obj.ContainsKey("Password"))
            obj["Password"] = null;
        return node;
    }
}
