using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MT.Uptime.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHeartbeatDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Diagnostics",
                table: "Heartbeats",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Diagnostics",
                table: "Heartbeats");
        }
    }
}
