using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace COMS_MVC.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoFactorTrustedDevicesAndPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SensorReadings_CanalId",
                table: "SensorReadings");

            migrationBuilder.DropIndex(
                name: "IX_FloodRiskAssessments_CanalId",
                table: "FloodRiskAssessments");

            migrationBuilder.CreateTable(
                name: "TrustedDevices",
                columns: table => new
                {
                    TrustedDeviceId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrustedDevices", x => x.TrustedDeviceId);
                    table.ForeignKey(
                        name: "FK_TrustedDevices_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_CanalId_RecordedAt",
                table: "SensorReadings",
                columns: new[] { "CanalId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FloodRiskAssessments_CanalId_AssessmentDate",
                table: "FloodRiskAssessments",
                columns: new[] { "CanalId", "AssessmentDate" });

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_TokenHash",
                table: "TrustedDevices",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_UserId_ExpiresAtUtc",
                table: "TrustedDevices",
                columns: new[] { "UserId", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrustedDevices");

            migrationBuilder.DropIndex(
                name: "IX_SensorReadings_CanalId_RecordedAt",
                table: "SensorReadings");

            migrationBuilder.DropIndex(
                name: "IX_FloodRiskAssessments_CanalId_AssessmentDate",
                table: "FloodRiskAssessments");

            migrationBuilder.CreateIndex(
                name: "IX_SensorReadings_CanalId",
                table: "SensorReadings",
                column: "CanalId");

            migrationBuilder.CreateIndex(
                name: "IX_FloodRiskAssessments_CanalId",
                table: "FloodRiskAssessments",
                column: "CanalId");
        }
    }
}
