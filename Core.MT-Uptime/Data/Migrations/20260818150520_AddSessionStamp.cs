using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MT.Uptime.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SessionStamp",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionStamp",
                table: "Users");
        }
    }
}
