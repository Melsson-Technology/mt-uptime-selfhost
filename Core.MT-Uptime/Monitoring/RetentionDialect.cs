namespace MT.Uptime.Core.Monitoring;

/// <summary>
/// The handful of things <see cref="RetentionService"/> cannot phrase the same way on every database.
/// <para>
/// Retention is the only part of the engine that drops out of LINQ and writes SQL by hand, and it does
/// so for good reasons — a set-based rollup that never materialises a heartbeat, and a bounded delete
/// whose write lock stays short. The cost is that four separate details stop being portable, and every
/// one of them was written for SQLite alone. Naming them here, once, is what stops the next person
/// discovering them one production stack trace at a time; the list below is the list that was found
/// that way on 2026-09-05.
/// </para>
/// <para>
/// <b>Two dialects are supported and tested: SQLite and MySQL.</b> Anything else falls back to
/// <see cref="Ansi"/>, which is what an unrecognised provider silently got before this type existed —
/// portable string concatenation and a <c>LIMIT</c> inside a subquery — minus the SQLite-only reclaim
/// step, which could only ever have thrown. That is a strict improvement on the old behaviour, not a
/// claim of support.
/// </para>
/// </summary>
internal sealed record RetentionDialect(
    string HourlyBucket,
    string DailyBucket,
    string BatchDeleteFormat,
    bool CanReclaimFreePages)
{
    /// <summary>
    /// SQLite. Heartbeat timestamps are stored as fixed-width text (<c>yyyy-MM-dd HH:mm:ss[.fff]</c>),
    /// so the bucket key is a string slice; <c>||</c> is concatenation; and <c>DELETE ... LIMIT</c> is a
    /// compile-time option that is not always present, which is why the delete goes through a subquery.
    /// </summary>
    internal static readonly RetentionDialect Sqlite = new(
        HourlyBucket: "substr(Timestamp, 1, 13) || ':00:00'",
        DailyBucket: "substr(Timestamp, 1, 10) || ' 00:00:00'",
        BatchDeleteFormat:
            "DELETE FROM Heartbeats WHERE Id IN (SELECT Id FROM Heartbeats WHERE Timestamp < {0} LIMIT {1})",
        CanReclaimFreePages: true);

    /// <summary>
    /// MySQL. Three of the four differences are not stylistic — they are the difference between working
    /// and not:
    /// <list type="bullet">
    ///   <item><b><c>||</c> is logical OR</b>, not concatenation, unless the session happens to run with
    ///     <c>PIPES_AS_CONCAT</c>. The SQLite bucket expression does not fail here — it quietly returns
    ///     <c>0</c> or <c>1</c> for every row, and the reader then fails parsing that as a timestamp.
    ///     <c>DATE_FORMAT</c> says what was meant, and truncates a real <c>DATETIME</c> rather than
    ///     relying on how it renders as text.</item>
    ///   <item><b><c>LIMIT</c> is not allowed inside an <c>IN</c> subquery</b> — MySQL rejects it
    ///     outright with "doesn't yet support 'LIMIT &amp; IN/ALL/ANY/SOME subquery'". It does support
    ///     <c>DELETE ... LIMIT</c> directly, which is what the subquery was working around in the first
    ///     place, so this form is both valid and simpler.</item>
    ///   <item><b>There is no cheap reclaim.</b> InnoDB returns freed pages to the tablespace by itself.
    ///     <c>OPTIMIZE TABLE</c> rebuilds the table and holds a lock for the duration, which is not
    ///     something to run unattended — least of all on a box shared with other services.</item>
    /// </list>
    /// </summary>
    internal static readonly RetentionDialect MySql = new(
        HourlyBucket: "DATE_FORMAT(Timestamp, '%Y-%m-%d %H:00:00')",
        DailyBucket: "DATE_FORMAT(Timestamp, '%Y-%m-%d 00:00:00')",
        BatchDeleteFormat: "DELETE FROM Heartbeats WHERE Timestamp < {0} LIMIT {1}",
        CanReclaimFreePages: false);

    /// <summary>
    /// The fallback for a provider this engine does not know. Same shape as <see cref="Sqlite"/>, which
    /// is ANSI enough to have worked by accident on PostgreSQL, without the reclaim step.
    /// </summary>
    internal static readonly RetentionDialect Ansi = Sqlite with { CanReclaimFreePages = false };

    /// <summary>
    /// Picks the dialect from EF's provider name.
    /// <para>
    /// Substring matching on the provider name, rather than <c>IsSqlite()</c>/<c>IsMySql()</c>, because
    /// those extension methods live in the provider packages and <c>Core</c> deliberately references
    /// only the SQLite one, which is this project's default. Substring matching also has the advantage
    /// of covering both MySQL providers in circulation, Oracle's and Pomelo's, whose provider names
    /// differ but both contain "MySql".
    /// </para>
    /// </summary>
    internal static RetentionDialect For(string? providerName) => providerName switch
    {
        null => Ansi,
        var p when p.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) => Sqlite,
        var p when p.Contains("MySql", StringComparison.OrdinalIgnoreCase) => MySql,
        _ => Ansi,
    };

    /// <summary>
    /// The bucket-key expression for one rollup period. Both forms produce exactly
    /// <c>yyyy-MM-dd HH:mm:ss</c>, because <see cref="RetentionService"/> parses the result with that
    /// format and nothing else — a dialect that returned a different shape would parse-fail per row.
    /// </summary>
    internal string BucketFor(RollupPeriod period) =>
        period == RollupPeriod.Hourly ? HourlyBucket : DailyBucket;

    /// <summary>
    /// The bounded delete, with the cutoff left as EF's <c>{0}</c> placeholder so it is parameterised,
    /// and the batch size substituted as a literal.
    /// <para>
    /// The batch size is a compile-time constant, never input, so inlining it is safe — and it avoids
    /// asking whether a given provider will accept a parameter in <c>LIMIT</c>, which is a question with
    /// a different answer per driver and per prepare mode.
    /// </para>
    /// </summary>
    internal string BatchDeleteSql(int batchSize) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, BatchDeleteFormat, "{0}", batchSize);
}
