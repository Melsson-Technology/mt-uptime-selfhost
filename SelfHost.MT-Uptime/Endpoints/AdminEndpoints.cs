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

            // Staged beside the database, NOT in Path.GetTempPath(). The state directory is 0700; the
            // shared /tmp is not, and SQLite creates the copy 0644 — so on a host with any other local
            // account this handed out a complete database (every password hash, every push token in the
            // clear, the whole monitored-infrastructure inventory) for the length of the download, and
            // permanently if the process was killed mid-stream. .NET emulates DeleteOnClose with an
            // unlink at close rather than at open, so the file is genuinely visible for that whole window.
            var stagingDirectory = Path.GetDirectoryName(databasePath)!;
            var tmp = Path.Combine(stagingDirectory, $"mt-uptime-backup-{stamp}-{Guid.NewGuid():N}.db");

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

            // Belt and braces on top of the 0700 directory: make the copy itself owner-only, so an
            // install that put its state somewhere more permissive is still covered.
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch (IOException) { /* best effort; the directory mode is the real guarantee */ }
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

    /// <summary>
    /// Every field across the monitor config types that carries a credential. Named rather than
    /// pattern-matched, so adding a secret field to a config is a deliberate edit here too — the
    /// alternative, guessing from the field name, fails silently in the direction that leaks.
    /// </summary>
    private static readonly string[] SecretConfigFields =
    [
        "Password",      // DbMonitorConfig — MySQL/Postgres
        "AuthSecret",    // HttpMonitorConfig — Basic password or Bearer token
        "Headers",       // HttpMonitorConfig — where API keys actually end up
        "Token",         // PushMonitorConfig — the ping credential, and the only one stored in the clear
    ];

    /// <summary>
    /// Parses a monitor's ConfigJson and nulls out every secret field before it leaves the box.
    /// <para>
    /// This file is meant to be handed around — copied to a laptop, attached to a ticket, kept as a
    /// record of what was configured — which is exactly why it must not carry credentials. Only
    /// <c>Password</c> was redacted before, so an export also shipped the encrypted HTTP auth secret and
    /// header block, and the push monitor's ping token <b>in the clear</b>: that token is a bearer
    /// credential anyone can use to forge heartbeats and suppress a monitor's outage alerts.
    /// </para>
    /// <para>
    /// The database backup from the sibling endpoint necessarily still contains all of these — it is a
    /// byte copy — so a backup has to be treated as the credentials it carries. This endpoint is the one
    /// whose whole purpose is to leave the instance.
    /// </para>
    /// </summary>
    private static JsonNode? Redact(string configJson)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(configJson); }
        catch { return null; }

        if (node is not JsonObject obj) return node;

        foreach (var field in SecretConfigFields)
            if (obj.ContainsKey(field))
                obj[field] = null;

        return node;
    }
}
