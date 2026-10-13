using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace COMS_MVC.Migrations
{
    /// <inheritdoc />
    public partial class AddImageRelevanceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ImageRelevanceConfidence",
                table: "CommunityReports",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageRelevanceReason",
                table: "CommunityReports",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageRelevanceStatus",
                table: "CommunityReports",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageSuggestedCategory",
                table: "CommunityReports",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageRelevanceConfidence",
                table: "CommunityReports");

            migrationBuilder.DropColumn(
                name: "ImageRelevanceReason",
                table: "CommunityReports");

            migrationBuilder.DropColumn(
                name: "ImageRelevanceStatus",
                table: "CommunityReports");

            migrationBuilder.DropColumn(
                name: "ImageSuggestedCategory",
                table: "CommunityReports");
        }
    }
}
