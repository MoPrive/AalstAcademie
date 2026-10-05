using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AalstAcademie.Web.Data.Migrations
{
    /// <summary>
    /// Sprint 002 breidt Identity uit met aanvraag- en reviewmetadata.
    /// Bestaande wachtwoorden, rollen, profielen en goedkeuringsstatussen blijven behouden.
    /// </summary>
    public partial class AddAccountReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Alleen nullable metadata toevoegen: bestaande accounts/credentials blijven behouden.
            // Een ontbrekende datum betekent een historische/onvolledige aanvraag; geen datum verzinnen.
            migrationBuilder.AddColumn<DateTime>(
                name: "AccountRequestedAtUtc",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            // De marker identificeert uitsluitend gereserveerde fictieve seeds, los van e-mail en accounttype.
            migrationBuilder.AddColumn<string>(
                name: "DemoSeedKey",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            // De actuele weigeringreden wordt later aan de aanvrager op zijn eigen statuspagina getoond.
            migrationBuilder.AddColumn<string>(
                name: "RefusalReason",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            // Een unieke SQLite-index laat meerdere nulls toe: gewone accounts hebben geen demomarker.
            // Twee gemarkeerde accounts kunnen nooit dezelfde gereserveerde seed voorstellen.
            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_DemoSeedKey",
                table: "AspNetUsers",
                column: "DemoSeedKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Een expliciete downgrade verwijdert uitsluitend de metadata van deze migratie.
            // Down herstelt geen mislukte review; de servicetransactie verzorgt die rollback.
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_DemoSeedKey",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AccountRequestedAtUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DemoSeedKey",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RefusalReason",
                table: "AspNetUsers");
        }
    }
}
