using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Domain;
using MT.Uptime.Web.Services;

namespace MT.Uptime.Tests;

/// <summary>
/// <c>Users.Email</c> is unique and case-insensitive, and a password reset resolves an account by it.
/// <para>
/// The security review chased this as a Viewer-to-Admin path and concluded it does not currently
/// escalate: the administrator is row 1, SQLite's AUTOINCREMENT never reuses a rowid, so an unordered
/// <c>FirstOrDefaultAsync</c> always returned the same row. That is an accident of insertion order
/// holding up an authentication boundary. These tests turn it into a rule, so it cannot quietly stop
/// being true.
/// </para>
/// </summary>
public class UserEmailUniquenessTests
{
    private static UserAccountService NewService(TestDatabase db)
        => new(db, new PasswordHasher<AppUser>());

    // --- The constraint ---------------------------------------------------------------------------

    [Fact]
    public async Task Two_accounts_cannot_share_an_email_address()
    {
        await using var db = await TestDatabase.CreateAsync();
        var svc = NewService(db);
        await svc.CreateAsync("first", "correct-horse", "shared@example.test", UserRole.Admin);

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => svc.CreateAsync("second", "correct-horse", "shared@example.test", UserRole.Viewer));
    }

    [Theory]
    [InlineData("Owner@Example.Test")]
    [InlineData("OWNER@EXAMPLE.TEST")]
    [InlineData("owner@example.test")]
    public async Task Addresses_differing_only_by_case_are_the_same_address(string variant)
    {
        await using var db = await TestDatabase.CreateAsync();
        var svc = NewService(db);
        await svc.CreateAsync("first", "correct-horse", "owner@example.test", UserRole.Admin);

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => svc.CreateAsync("second", "correct-horse", variant, UserRole.Viewer));
    }

    [Fact]
    public async Task Any_number_of_accounts_may_have_no_email_at_all()
    {
        // SQLite treats NULLs as distinct in a unique index, which is the behaviour being relied on here:
        // an email is optional, and making it unique must not quietly make it required.
        await using var db = await TestDatabase.CreateAsync();
        var svc = NewService(db);

        await svc.CreateAsync("one", "correct-horse", null, UserRole.Admin);
        await svc.CreateAsync("two", "correct-horse", null, UserRole.Viewer);
        await svc.CreateAsync("three", "correct-horse", "   ", UserRole.Viewer); // blank normalises to NULL

        await using var ctx = db.CreateDbContext();
        Assert.Equal(3, await ctx.Users.CountAsync());
        Assert.Equal(3, await ctx.Users.CountAsync(u => u.Email == null));
    }

    // --- The operator-facing paths ----------------------------------------------------------------
    //
    // The index is the guard. These assert that reaching it is not how an operator finds out, because a
    // constraint violation surfaces as an unhandled exception rather than a message on the form.

    [Fact]
    public async Task Adding_a_user_with_a_taken_address_is_refused_with_a_message()
    {
        await using var db = await TestDatabase.CreateAsync();
        var svc = NewService(db);
        await svc.CreateAsync("first", "correct-horse", "taken@example.test", UserRole.Admin);

        var error = await svc.AddUserAsync(
            "second", "correct-horse", "Second", "TAKEN@example.test", UserRole.Editor);

        Assert.NotNull(error);
        Assert.Contains("email", error, StringComparison.OrdinalIgnoreCase);
        await using var ctx = db.CreateDbContext();
        Assert.Equal(1, await ctx.Users.CountAsync());
    }

    [Fact]
    public async Task Editing_a_profile_cannot_take_another_accounts_address()
    {
        await using var db = await TestDatabase.CreateAsync();
        var svc = NewService(db);
        await svc.CreateAsync("first", "correct-horse", "first@example.test", UserRole.Admin);
        var second = await svc.CreateAsync("second", "correct-horse", "second@example.test", UserRole.Editor);

        var error = await svc.UpdateProfileAsync(second.Id, "second", "Second", "first@example.test");

        Assert.NotNull(error);
        Assert.Contains("email", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Saving_your_own_profile_unchanged_is_not_a_collision_with_yourself()
    {
        // The exclusion of one's own row is the whole reason UpdateProfileAsync's check differs from
        // AddUserAsync's. Without it, editing a display name would report the address as taken.
        await using var db = await TestDatabase.CreateAsync();
        var svc = NewService(db);
        var user = await svc.CreateAsync("only", "correct-horse", "only@example.test", UserRole.Admin);

        Assert.Null(await svc.UpdateProfileAsync(user.Id, "only", "New Name", "only@example.test"));
    }

    // --- Reset resolution -------------------------------------------------------------------------

    [Fact]
    public async Task A_reset_resolves_the_account_whatever_case_the_address_is_typed_in()
    {
        // Without NOCASE this returns null, and /auth/forgot answers identically either way by design —
        // so the operator is told a link is on its way and none ever arrives, on the one path that
        // exists to recover an account nobody can sign into.
        await using var db = await TestDatabase.CreateAsync();
        var svc = NewService(db);
        await svc.CreateAsync("owner", "correct-horse", "Owner@Example.Test", UserRole.Admin);

        Assert.NotNull(await svc.BeginPasswordResetAsync("owner@example.test"));
        Assert.NotNull(await svc.BeginPasswordResetAsync("OWNER@EXAMPLE.TEST"));
        Assert.Null(await svc.BeginPasswordResetAsync("someone.else@example.test"));
    }

    // --- The upgrade ------------------------------------------------------------------------------

    /// <summary>The migration immediately before AddUniqueUserEmail — the state a live install upgrades from.</summary>
    private const string PreviousMigration = "20260818150520_AddSessionStamp";

    [Fact]
    public async Task Upgrading_an_install_that_already_has_duplicate_addresses_does_not_fail()
    {
        // The scaffolded migration went straight to CreateIndex(unique: true), which throws here. A
        // migration throws at startup, so that does not merely fail the upgrade — it stops the instance
        // booting, for someone whose only clue is a constraint name. Every other test creates its
        // database at the latest migration, where the index already exists, so only replaying the
        // upgrade can see this.
        var path = Path.Combine(Path.GetTempPath(), $"mt-uptime-email-migrate-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString())
            .Options;

        try
        {
            await using (var db = new AppDbContext(options))
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            // Three accounts sharing one address in two different cases, plus one that does not, plus
            // two with none. Raw SQL because the model at this point has no unique index to fight.
            await using (var db = new AppDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO Users (Username, PasswordHash, CreatedAt, Role, SessionStamp, Email)
                    VALUES ('admin',     'hash', '2026-01-01 00:00:00', 2, 0, 'shared@example.test'),
                           ('duplicate', 'hash', '2026-01-02 00:00:00', 1, 0, 'SHARED@EXAMPLE.TEST'),
                           ('third',     'hash', '2026-01-03 00:00:00', 0, 0, 'shared@example.test'),
                           ('distinct',  'hash', '2026-01-04 00:00:00', 0, 0, 'other@example.test'),
                           ('nomail',    'hash', '2026-01-05 00:00:00', 0, 0, NULL),
                           ('nomail2',   'hash', '2026-01-06 00:00:00', 0, 0, NULL);
                    """);
            }

            await using (var db = new AppDbContext(options))
                await db.Database.MigrateAsync();

            await using (var verify = new AppDbContext(options))
            {
                // Nobody is deleted — only the address is surrendered.
                Assert.Equal(6, await verify.Users.CountAsync());

                // The lowest Id keeps it, which is the account BeginPasswordResetAsync's OrderBy would
                // have chosen before the upgrade. So the reset link goes where it always went.
                var admin = await verify.Users.SingleAsync(u => u.Username == "admin");
                Assert.Equal("shared@example.test", admin.Email);

                foreach (var loser in new[] { "duplicate", "third" })
                    Assert.Null((await verify.Users.SingleAsync(u => u.Username == loser)).Email);

                // An address that was never duplicated is untouched, and NULLs are left alone.
                Assert.Equal("other@example.test",
                    (await verify.Users.SingleAsync(u => u.Username == "distinct")).Email);
                // Four: the two duplicates surrendered above, plus the two that never had an address.
                Assert.Equal(4, await verify.Users.CountAsync(u => u.Email == null));

                // And the constraint is genuinely in force afterwards.
                verify.Users.Add(new AppUser
                {
                    Username = "late",
                    PasswordHash = "hash",
                    CreatedAt = DateTime.UtcNow,
                    Role = UserRole.Viewer,
                    Email = "other@example.test",
                });
                await Assert.ThrowsAnyAsync<DbUpdateException>(() => verify.SaveChangesAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
