using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MT.Uptime.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "IncidentId",
                table: "MonitorEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CorrelationKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastEventAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    MonitorCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcknowledgedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    SnoozedUntil = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Incidents_Users_AcknowledgedByUserId",
                        column: x => x.AcknowledgedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitorEvents_IncidentId",
                table: "MonitorEvents",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_AcknowledgedByUserId",
                table: "Incidents",
                column: "AcknowledgedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_CorrelationKey_ResolvedAt",
                table: "Incidents",
                columns: new[] { "CorrelationKey", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_StartedAt",
                table: "Incidents",
                column: "StartedAt");

            migrationBuilder.AddForeignKey(
                name: "FK_MonitorEvents_Incidents_IncidentId",
                table: "MonitorEvents",
                column: "IncidentId",
                principalTable: "Incidents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MonitorEvents_Incidents_IncidentId",
                table: "MonitorEvents");

            migrationBuilder.DropTable(
                name: "Incidents");

            migrationBuilder.DropIndex(
                name: "IX_MonitorEvents_IncidentId",
                table: "MonitorEvents");

            migrationBuilder.DropColumn(
                name: "IncidentId",
                table: "MonitorEvents");
        }
    }
}
