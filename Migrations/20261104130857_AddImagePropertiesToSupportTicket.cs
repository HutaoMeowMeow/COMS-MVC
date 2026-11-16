using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace COMS_MVC.Migrations
{
    /// <inheritdoc />
    public partial class AddImagePropertiesToSupportTicket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageExtension",
                table: "SupportTickets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImagePath",
                table: "SupportTickets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ImageSizeBytes",
                table: "SupportTickets",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageExtension",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "ImagePath",
                table: "SupportTickets");

            migrationBuilder.DropColumn(
                name: "ImageSizeBytes",
                table: "SupportTickets");
        }
    }
}
