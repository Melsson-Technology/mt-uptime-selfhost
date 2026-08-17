using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MT.Uptime.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMaintenanceWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaintenanceCount",
                table: "StatRollups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Maintenance",
                table: "Heartbeats",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MaintenanceWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Recurrence = table.Column<int>(type: "INTEGER", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndsAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StartMinuteOfDay = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    DaysOfWeekMask = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AppliesToAllMonitors = table.Column<bool>(type: "INTEGER", nullable: false),
                    Publish = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWindowMonitors",
                columns: table => new
                {
                    MaintenanceWindowId = table.Column<int>(type: "INTEGER", nullable: false),
                    MonitorId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindowMonitors", x => new { x.MaintenanceWindowId, x.MonitorId });
                    table.ForeignKey(
                        name: "FK_MaintenanceWindowMonitors_MaintenanceWindows_MaintenanceWindowId",
                        column: x => x.MaintenanceWindowId,
                        principalTable: "MaintenanceWindows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaintenanceWindowMonitors_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWindowTags",
                columns: table => new
                {
                    MaintenanceWindowId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindowTags", x => new { x.MaintenanceWindowId, x.TagId });
                    table.ForeignKey(
                        name: "FK_MaintenanceWindowTags_MaintenanceWindows_MaintenanceWindowId",
                        column: x => x.MaintenanceWindowId,
                        principalTable: "MaintenanceWindows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaintenanceWindowTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindowMonitors_MonitorId",
                table: "MaintenanceWindowMonitors",
                column: "MonitorId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindows_Enabled",
                table: "MaintenanceWindows",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindowTags_TagId",
                table: "MaintenanceWindowTags",
                column: "TagId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceWindowMonitors");

            migrationBuilder.DropTable(
                name: "MaintenanceWindowTags");

            migrationBuilder.DropTable(
                name: "MaintenanceWindows");

            migrationBuilder.DropColumn(
                name: "MaintenanceCount",
                table: "StatRollups");

            migrationBuilder.DropColumn(
                name: "Maintenance",
                table: "Heartbeats");
        }
    }
}
