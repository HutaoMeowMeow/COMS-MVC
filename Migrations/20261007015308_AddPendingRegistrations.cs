using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace COMS_MVC.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.CreateTable(
                name: "PendingRegistrations",
                columns: table => new
                {
                    PendingRegistrationId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FullName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PhoneNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Barangay = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Address = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    Role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    OtpHash = table.Column<string>(type: "text", nullable: true),
                    OtpExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastSentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RequestCount = table.Column<int>(type: "integer", nullable: false),
                    RequestWindowStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingRegistrations", x => x.PendingRegistrationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_ExpiresAtUtc",
                table: "PendingRegistrations",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_NormalizedEmail",
                table: "PendingRegistrations",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingRegistrations_NormalizedUserName",
                table: "PendingRegistrations",
                column: "NormalizedUserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingRegistrations");

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
    }
}
