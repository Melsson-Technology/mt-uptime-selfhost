using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MT.Uptime.Core.Data;

/// <summary>
/// Applies per-connection SQLite PRAGMAs every time a connection opens:
/// <list type="bullet">
///   <item>WAL journal — readers don't block the single writer.</item>
///   <item>synchronous=NORMAL — safe with WAL, far fewer fsyncs.</item>
///   <item>busy_timeout=5000 — wait out brief write locks instead of failing with "database is locked".</item>
///   <item>foreign_keys=ON — SQLite leaves FK enforcement off by default.</item>
/// </list>
/// Re-asserting journal_mode=WAL per connection is a cheap no-op once the database is already in WAL mode.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas =
        "PRAGMA journal_mode=WAL;" +
        "PRAGMA synchronous=NORMAL;" +
        "PRAGMA busy_timeout=5000;" +
        "PRAGMA foreign_keys=ON;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
