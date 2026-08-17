using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MT.Uptime.Core.Data.Migrations
{
    /// <summary>
    /// Makes <c>Users.Username</c> compare case-insensitively, so signing in as "matt" reaches the account
    /// created as "Matt" instead of being reported as a bad password.
    /// <para>
    /// SQLite cannot alter a column in place, so EF rebuilds the table; the unique index on Username is
    /// recreated and inherits the new collation, which makes uniqueness case-insensitive too.
    /// <b>On an existing install that already holds two accounts differing only by case, that index will
    /// fail to build and this migration will not apply.</b> Rename one of them first — they were never
    /// safely distinguishable anyway.
    /// </para>
    /// </summary>
    public partial class CaseInsensitiveUsernames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "Users",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "Users",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256,
                oldCollation: "NOCASE");
        }
    }
}
