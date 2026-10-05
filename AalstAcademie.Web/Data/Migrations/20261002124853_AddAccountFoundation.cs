using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AalstAcademie.Web.Data.Migrations
{
    /// <summary>
    /// Gegenereerde schemawijziging voor Sprint 001: breidt de bestaande Identity-opslag uit.
    /// De oorspronkelijke migratie blijft behouden; deze migratie bevat alleen de nieuwe stap.
    /// </summary>
    public partial class AddAccountFoundation : Migration
    {
        /// <summary>Brengt de database naar het accountfoundation-schema.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Bestaande accounts krijgen standaard Pending (0), nooit automatisch Approved.
            migrationBuilder.AddColumn<int>(
                name: "AccountApprovalStatus",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Nullable omdat oudere accounts nog geen geverifieerde profielgegevens hebben.
            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            // Namen blijven optioneel in het schema; de pure regels toetsen volledige profielen.
            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            // Nieuwe en gemigreerde accounts starten met een niet-geblokkeerde vlag.
            migrationBuilder.AddColumn<bool>(
                name: "IsBlocked",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            // Leidinggevende van de medewerker; geen eigenaar/beheerder van een opleiding.
            migrationBuilder.AddColumn<string>(
                name: "ManagerName",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            // Enumgetal voor het aangevraagde type; null representeert een incomplete/oudere gebruiker.
            migrationBuilder.AddColumn<int>(
                name: "RequestedAccountType",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            // Beoordelingstijd en beoordelaar horen bij de nog te bouwen goedkeuringsworkflow.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedById",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            // Referentietabel met eigen primaire sleutel en controle op een bruikbare naam.
            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                    table.CheckConstraint("CK_Department_Name", "length(trim(Name)) BETWEEN 1 AND 100");
                });

            // Organisatiegegevens op het externe profiel; de FK vereist een bestaand account.
            migrationBuilder.CreateTable(
                name: "ExternalInstructors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ApplicationUserId = table.Column<string>(type: "TEXT", nullable: false),
                    OrganizationName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    VatNumber = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalInstructors", x => x.Id);
                    table.CheckConstraint("CK_ExternalInstructor_Name", "length(trim(OrganizationName)) BETWEEN 1 AND 200");
                    table.CheckConstraint("CK_ExternalInstructor_Vat", "VatNumber IS NULL OR length(VatNumber) <= 32");
                    table.ForeignKey(
                        name: "FK_ExternalInstructors_AspNetUsers_ApplicationUserId",
                        column: x => x.ApplicationUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Het interne profiel bevat alleen de accountlink; namen/afdeling staan op de gebruiker.
            migrationBuilder.CreateTable(
                name: "InternalInstructors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ApplicationUserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalInstructors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InternalInstructors_AspNetUsers_ApplicationUserId",
                        column: x => x.ApplicationUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Indexen helpen bij relatiequeries; unieke indexen voorkomen dubbele naam/profiellink.
            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_DepartmentId",
                table: "AspNetUsers",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ReviewedById",
                table: "AspNetUsers",
                column: "ReviewedById");

            migrationBuilder.CreateIndex(
                name: "IX_Departments_Name",
                table: "Departments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalInstructors_ApplicationUserId",
                table: "ExternalInstructors",
                column: "ApplicationUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InternalInstructors_ApplicationUserId",
                table: "InternalInstructors",
                column: "ApplicationUserId",
                unique: true);

            // Restrict verhindert verwijdering van een gebruikte beoordelaar of afdeling.
            // EF kan voor deze FK-uitbreiding de SQLite-gebruikerstabel opnieuw opbouwen.
            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_AspNetUsers_ReviewedById",
                table: "AspNetUsers",
                column: "ReviewedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Departments_DepartmentId",
                table: "AspNetUsers",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <summary>
        /// Schema-terugweg voor tooling. Verwijdert de foundationtabellen/kolommen,
        /// inclusief hun gegevens; dit is geen herstel van eerder ingevulde profieldata.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Verwijder eerst de FK-afhankelijkheden vóór de betrokken tabel/kolommen verdwijnen.
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_AspNetUsers_ReviewedById",
                table: "AspNetUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Departments_DepartmentId",
                table: "AspNetUsers");

            // Alleen de in deze stap toegevoegde tabellen worden terug verwijderd.
            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropTable(
                name: "ExternalInstructors");

            migrationBuilder.DropTable(
                name: "InternalInstructors");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_DepartmentId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_ReviewedById",
                table: "AspNetUsers");

            // Oude Identity-kolommen blijven staan; de nieuwe profiel/beoordelingsvelden verdwijnen.
            migrationBuilder.DropColumn(
                name: "AccountApprovalStatus",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "FirstName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "IsBlocked",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LastName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ManagerName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RequestedAccountType",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ReviewedById",
                table: "AspNetUsers");
        }
    }
}
