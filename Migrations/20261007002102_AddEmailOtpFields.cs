using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace COMS_MVC.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailOtpFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmailOtpExpiresAtUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmailOtpFailedAttempts",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EmailOtpHash",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailOtpLastSentAtUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmailOtpRequestCount",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailOtpRequestWindowStartUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailOtpExpiresAtUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailOtpFailedAttempts",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailOtpHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailOtpLastSentAtUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailOtpRequestCount",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EmailOtpRequestWindowStartUtc",
                table: "AspNetUsers");
        }
    }
}
