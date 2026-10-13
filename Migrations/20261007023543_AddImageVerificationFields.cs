using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace COMS_MVC.Migrations
{
    /// <inheritdoc />
    public partial class AddImageVerificationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ImageVerificationAnalyzedAt",
                table: "CommunityReports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageVerificationConfidence",
                table: "CommunityReports",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageVerificationReason",
                table: "CommunityReports",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageVerificationStatus",
                table: "CommunityReports",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageVerificationAnalyzedAt",
                table: "CommunityReports");

            migrationBuilder.DropColumn(
                name: "ImageVerificationConfidence",
                table: "CommunityReports");

            migrationBuilder.DropColumn(
                name: "ImageVerificationReason",
                table: "CommunityReports");

            migrationBuilder.DropColumn(
                name: "ImageVerificationStatus",
                table: "CommunityReports");
        }
    }
}
