using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MT.Uptime.Core.Data.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Makes <c>Users.Email</c> case-insensitive and unique, because it is what a password reset
    /// resolves an account by.
    /// <para>
    /// <b>The scaffolded migration was not safe to ship as generated.</b> It went straight from
    /// <c>AlterColumn</c> to <c>CreateIndex(unique: true)</c>, which throws on any existing database
    /// that already has two accounts sharing an address — and a migration that throws is applied at
    /// startup, so it does not merely fail: it stops the instance from booting, on an upgrade, for
    /// somebody who has no idea why. That is the <c>AddUserRoles</c> lesson, where the generated
    /// migration would have demoted the only administrator.
    /// </para>
    /// <para>
    /// So duplicates are resolved first, deterministically: the <b>lowest Id keeps the address</b> and
    /// the rest are set to NULL. That rule is chosen to match <c>BeginPasswordResetAsync</c>'s
    /// <c>OrderBy(u =&gt; u.Id)</c> — the account that would have received the reset link before this
    /// migration is the one that can still receive it afterwards, so the upgrade changes nothing an
    /// operator could observe except that the ambiguity is gone.
    /// </para>
    /// <para>
    /// Clearing an address is a real data change, and it is the right one here: two accounts with the
    /// same email meant "reset the password for this address" already had no defined answer. Any
    /// account emptied this way simply has no address, which is a supported state — NULLs are exempt
    /// from the unique index, since SQLite treats them as distinct.
    /// </para>
    /// </remarks>
    public partial class AddUniqueUserEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // De-duplicate FIRST, before the AlterColumn below.
            //
            // Ordering is not cosmetic here. Adding a collation to a column is, on SQLite, a full table
            // rebuild rather than an in-place change, and EF defers that rebuild — so raw SQL written
            // after the AlterColumn executes while a rebuild of Users is pending. EF says so out loud on
            // every boot: "An operation of type 'SqlOperation' will be attempted while a rebuild of table
            // 'Users' is pending. The database may not be in an expected state." The tests passed either
            // way, which is exactly why this was worth reading a container's startup log to find.
            //
            // Running it first means no rebuild is outstanding and the statement sees an ordinary table.
            //
            // COLLATE NOCASE is therefore load-bearing rather than defensive: at this point the column
            // still carries the old binary collation, and without it "Matt@example.com" and
            // "matt@example.com" would be counted as two distinct addresses, survive de-duplication, and
            // then collide when the unique index is created a few lines below — which is the exact
            // failure this whole block exists to prevent.
            migrationBuilder.Sql("""
                UPDATE Users
                SET Email = NULL
                WHERE Email IS NOT NULL
                  AND Id NOT IN (
                      SELECT MIN(Id) FROM Users WHERE Email IS NOT NULL GROUP BY Email COLLATE NOCASE
                  );
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Users",
                type: "TEXT",
                maxLength: 320,
                nullable: true,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 320,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Users",
                type: "TEXT",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 320,
                oldNullable: true,
                oldCollation: "NOCASE");
        }
    }
}
