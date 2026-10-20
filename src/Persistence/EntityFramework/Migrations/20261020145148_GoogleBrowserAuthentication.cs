using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MUnique.OpenMU.Persistence.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class GoogleBrowserAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BrowserLoginTicketExpiresAt",
                schema: "data",
                table: "Account",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BrowserLoginTicketHash",
                schema: "data",
                table: "Account",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BrowserSessionExpiresAt",
                schema: "data",
                table: "Account",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BrowserSessionTokenHash",
                schema: "data",
                table: "Account",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleSubject",
                schema: "data",
                table: "Account",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Account_BrowserLoginTicketHash",
                schema: "data",
                table: "Account",
                column: "BrowserLoginTicketHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Account_BrowserSessionTokenHash",
                schema: "data",
                table: "Account",
                column: "BrowserSessionTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Account_GoogleSubject",
                schema: "data",
                table: "Account",
                column: "GoogleSubject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Account_BrowserLoginTicketHash",
                schema: "data",
                table: "Account");

            migrationBuilder.DropIndex(
                name: "IX_Account_BrowserSessionTokenHash",
                schema: "data",
                table: "Account");

            migrationBuilder.DropIndex(
                name: "IX_Account_GoogleSubject",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "BrowserLoginTicketExpiresAt",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "BrowserLoginTicketHash",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "BrowserSessionExpiresAt",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "BrowserSessionTokenHash",
                schema: "data",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "GoogleSubject",
                schema: "data",
                table: "Account");
        }
    }
}
