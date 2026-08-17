using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MT.Uptime.Core.Data;
using MT.Uptime.Core.Domain;

namespace MT.Uptime.Tests;

/// <summary>
/// The AddUserRoles migration, applied to a database that already has accounts in it.
/// <para>
/// This is the one upgrade step that can lock an operator out of their own instance. The Role column's
/// default is Viewer, which is correct for a new row and wrong for every existing one: before roles, the
/// product was single-admin, so everyone already in the table is an administrator. Take the backfill out
/// and the only account on a live install silently becomes read-only — and since promoting someone
/// requires an admin, there is no way back through the UI.
/// </para>
/// <para>
/// Every other test creates its database at the latest migration, where the column already exists with a
/// value, so none of them can see this. Only replaying the upgrade can.
/// </para>
/// </summary>
public class UserRoleMigrationTests
{
    /// <summary>The migration immediately before AddUserRoles — the state a live install upgrades from.</summary>
    private const string PreviousMigration = "20260814193613_AddDegradedAndUserProfile";

    [Fact]
    public async Task Upgrading_an_existing_install_leaves_its_accounts_as_administrators()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mt-uptime-migrate-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString())
            .Options;

        try
        {
            // 1. Bring the schema up to the pre-roles state.
            await using (var db = new AppDbContext(options))
                await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            // 2. Insert accounts the way a real install would have them. Raw SQL, not the entity model:
            //    AppUser has a Role property and the table at this point has no Role column.
            await using (var db = new AppDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO Users (Username, PasswordHash, CreatedAt, DisplayName, Email)
                    VALUES ('admin', 'hash', '2026-01-01 00:00:00', 'The Admin', 'admin@example.test'),
                           ('colleague', 'hash', '2026-01-02 00:00:00', NULL, NULL);
                    """);
            }

            // 3. Upgrade.
            await using (var db = new AppDbContext(options))
                await db.Database.MigrateAsync();

            // 4. Both pre-existing accounts must still be able to administer the instance.
            await using (var db = new AppDbContext(options))
            {
                var users = await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync();

                Assert.Equal(2, users.Count);
                Assert.All(users, u => Assert.Equal(UserRole.Admin, u.Role));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public async Task An_account_created_after_the_upgrade_defaults_to_the_weakest_role()
    {
        // The other half of the same decision: the column default must stay Viewer, so a code path that
        // forgets to pass a role cannot mint an administrator. The backfill above is a one-off for rows
        // that predate the column, not a general "everyone is an admin" rule.
        await using var db = await TestDatabase.CreateAsync();
        await using var ctx = db.CreateDbContext();

        ctx.Users.Add(new AppUser { Username = "fresh", PasswordHash = "hash", CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var created = await ctx.Users.AsNoTracking().SingleAsync(u => u.Username == "fresh");
        Assert.Equal(UserRole.Viewer, created.Role);
    }
}
