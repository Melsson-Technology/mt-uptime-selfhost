using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;

namespace MT.Uptime.Tests;

/// <summary>
/// Pins the hand-written SQL that retention emits, per database engine.
/// <para>
/// <b>Why this suite exists at all.</b> Every other test here runs on SQLite, which is the shipped
/// default — and every one of the defects this file guards was invisible on SQLite and fatal on MySQL.
/// One of them was not even fatal: <c>||</c> is concatenation in SQLite and <i>logical OR</i> in MySQL,
/// so the bucket expression returned <c>0</c> or <c>1</c> per row and the failure surfaced somewhere
/// else entirely. Asserting on the generated SQL is the only way to catch that class of thing without
/// a server, and a class of thing that reaches production once has earned a test that runs everywhere.
/// </para>
/// <para>
/// Running the cycle end to end against a real MySQL server needs both a server and a MySQL EF
/// provider, and this project references neither — SQLite is what it ships on. So the assertions here
/// are on the SQL rather than on its effect, which is the trade that lets them run on every build.
/// </para>
/// </summary>
public class RetentionDialectTests
{
    private const int BatchSize = 5000;

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite")]
    // The two MySQL providers in circulation: Oracle's and Pomelo's.
    [InlineData("MySql.EntityFrameworkCore")]
    [InlineData("Pomelo.EntityFrameworkCore.MySql")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData(null)]
    public void Every_provider_resolves_to_a_dialect(string? provider)
    {
        // Never null, never a throw: an engine nobody anticipated must degrade to the portable form
        // rather than take down a background service on a stranger's install.
        Assert.NotNull(RetentionDialect.For(provider));
    }

    [Fact]
    public void Provider_names_map_to_the_right_dialect()
    {
        Assert.Same(RetentionDialect.Sqlite, RetentionDialect.For("Microsoft.EntityFrameworkCore.Sqlite"));
        Assert.Same(RetentionDialect.MySql, RetentionDialect.For("MySql.EntityFrameworkCore"));
        Assert.Same(RetentionDialect.MySql, RetentionDialect.For("Pomelo.EntityFrameworkCore.MySql"));
        Assert.Same(RetentionDialect.Ansi, RetentionDialect.For("Npgsql.EntityFrameworkCore.PostgreSQL"));
        Assert.Same(RetentionDialect.Ansi, RetentionDialect.For(null));
    }

    [Theory]
    [InlineData(RollupPeriod.Hourly)]
    [InlineData(RollupPeriod.Daily)]
    public void MySql_never_concatenates_with_a_double_pipe(RollupPeriod period)
    {
        // The quiet one. In MySQL `||` is logical OR unless the session runs with PIPES_AS_CONCAT, so
        // the SQLite bucket expression does not error — it returns 0 or 1 for every row, and the failure
        // lands in DateTime.ParseExact, a long way from the cause.
        var sql = RetentionService.BuildRollupSql(RetentionDialect.MySql, period, hasFrom: false);

        Assert.DoesNotContain("||", sql, StringComparison.Ordinal);
        Assert.Contains("DATE_FORMAT(Timestamp,", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MySql_truncates_to_exactly_the_format_the_reader_parses()
    {
        // RetentionService reads the bucket back with ParseExact("yyyy-MM-dd HH:mm:ss"). A dialect that
        // truncated to anything else would parse-fail on every row, so these two patterns are not
        // cosmetic — they are the contract with the reader.
        Assert.Contains("'%Y-%m-%d %H:00:00'", RetentionDialect.MySql.HourlyBucket, StringComparison.Ordinal);
        Assert.Contains("'%Y-%m-%d 00:00:00'", RetentionDialect.MySql.DailyBucket, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RollupPeriod.Hourly)]
    [InlineData(RollupPeriod.Daily)]
    public void Rollup_parameters_use_the_portable_prefix(RollupPeriod period)
    {
        // The reported defect, exactly: "Unknown column '$boundary' in 'where clause'". SQLite accepts
        // @, $ and : alike, so `$` looked correct forever; MySQL accepts only `@`.
        foreach (var dialect in new[] { RetentionDialect.Sqlite, RetentionDialect.MySql, RetentionDialect.Ansi })
        {
            var sql = RetentionService.BuildRollupSql(dialect, period, hasFrom: true);

            Assert.DoesNotContain("$boundary", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("$from", sql, StringComparison.Ordinal);
            Assert.Contains("@boundary", sql, StringComparison.Ordinal);
            Assert.Contains("@from", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_resume_clause_appears_only_when_there_is_a_watermark()
    {
        var withFrom = RetentionService.BuildRollupSql(RetentionDialect.Sqlite, RollupPeriod.Daily, hasFrom: true);
        var without = RetentionService.BuildRollupSql(RetentionDialect.Sqlite, RollupPeriod.Daily, hasFrom: false);

        Assert.Contains("AND Timestamp >= @from", withFrom, StringComparison.Ordinal);
        Assert.DoesNotContain("@from", without, StringComparison.Ordinal);
    }

    [Fact]
    public void MySql_deletes_with_a_bare_limit_because_it_rejects_limit_in_a_subquery()
    {
        // MySQL: "This version of MySQL doesn't yet support 'LIMIT & IN/ALL/ANY/SOME subquery'".
        var sql = RetentionDialect.MySql.BatchDeleteSql(BatchSize);

        Assert.DoesNotContain("SELECT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT 5000", sql, StringComparison.Ordinal);
        Assert.Contains("Timestamp < {0}", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Sqlite_deletes_through_a_subquery_because_DELETE_LIMIT_is_optional_there()
    {
        // SQLite's DELETE...LIMIT needs SQLITE_ENABLE_UPDATE_DELETE_LIMIT at compile time, which the
        // bundled native library does not set.
        var sql = RetentionDialect.Sqlite.BatchDeleteSql(BatchSize);

        Assert.Contains("WHERE Id IN (SELECT Id FROM Heartbeats", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 5000)", sql, StringComparison.Ordinal);
        Assert.Contains("Timestamp < {0}", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cutoff_stays_a_parameter_and_the_batch_size_does_not()
    {
        // {0} is EF's positional placeholder, so the cutoff is parameterised. The batch size is inlined
        // deliberately: it is a compile-time constant, and whether a driver accepts a parameter in LIMIT
        // varies by provider and prepare mode.
        foreach (var dialect in new[] { RetentionDialect.Sqlite, RetentionDialect.MySql, RetentionDialect.Ansi })
        {
            var sql = dialect.BatchDeleteSql(BatchSize);
            Assert.Contains("{0}", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("{1}", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Only_sqlite_claims_it_can_reclaim_free_pages()
    {
        // wal_checkpoint and incremental_vacuum are PRAGMAs. Running them anywhere else is a syntax
        // error, and InnoDB returns freed pages to its tablespace without being asked.
        Assert.True(RetentionDialect.Sqlite.CanReclaimFreePages);
        Assert.False(RetentionDialect.MySql.CanReclaimFreePages);
        Assert.False(RetentionDialect.Ansi.CanReclaimFreePages);
    }
}
