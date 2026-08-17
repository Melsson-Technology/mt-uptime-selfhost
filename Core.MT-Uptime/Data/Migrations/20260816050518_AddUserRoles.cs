using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MT.Uptime.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // The column default is Viewer (0), which is right for a *new* row and catastrophic for the
            // existing ones: before this migration the product was single-admin, so every account already
            // in the table is an administrator. Without this backfill, upgrading demotes the only account
            // on the instance to read-only — and since promoting someone requires an admin, there is then
            // no way back in through the UI at all.
            //
            // Literal 2 rather than nameof(UserRole.Admin): a migration is a historical record of what was
            // applied, and must keep meaning this even if the enum is renumbered later.
            migrationBuilder.Sql("UPDATE Users SET Role = 2;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Role",
                table: "Users");
        }
    }
}
