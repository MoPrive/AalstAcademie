// Sprint 003: Voert de guarded vervolgmigratie uit naar definities, momenten, zalen en echte historieopslag
// Een SQL-guard weigert oude opleidingsrijen; Down weigert een niet-goedgekeurde destructieve terugweg.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AalstAcademie.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitTrainingDefinitionsAndMoments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Oude opleidingsrijen hebben geen goedgekeurde conversie: faal vóór duurzaam schemawerk.
            // TEMP en suppressTransaction:false laten de provider de eerste guard atomisch uitvoeren.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE __Revision02LegacyTrainingGuard
                 (Allowed INTEGER NOT NULL CONSTRAINT CK_Revision02_RequiresEmptyTraining CHECK(Allowed=1));
                INSERT INTO __Revision02LegacyTrainingGuard(Allowed)
                 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM main.Trainings) THEN 1 ELSE 0 END;
                DROP TABLE temp.__Revision02LegacyTrainingGuard;
                """, suppressTransaction: false);

            // EF-delta; categoriegegevens blijven behouden bij de sterkere bestaande Guid-vormcontrole.
            migrationBuilder.DropForeignKey(
                name: "FK_Trainings_ExternalInstructors_ExternalInstructorId",
                table: "Trainings");

            migrationBuilder.DropForeignKey(
                name: "FK_Trainings_InternalInstructors_InternalInstructorId",
                table: "Trainings");

            migrationBuilder.DropIndex(
                name: "IX_Trainings_ExternalInstructorId",
                table: "Trainings");

            migrationBuilder.DropIndex(
                name: "IX_Trainings_InternalInstructorId",
                table: "Trainings");

            migrationBuilder.DropIndex(
                name: "IX_Trainings_IsArchived_Date_StartTimeTicks_Id",
                table: "Trainings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Training_Booleans",
                table: "Trainings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Training_Capacity",
                table: "Trainings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Training_Date",
                table: "Trainings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Training_Description",
                table: "Trainings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Training_InstructorXor",
                table: "Trainings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Training_Location",
                table: "Trainings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Training_Price",
                table: "Trainings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Training_Time",
                table: "Trainings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Training_Version",
                table: "Trainings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Category_Version",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "EndTimeTicks",
                table: "Trainings");

            migrationBuilder.DropColumn(
                name: "ExternalInstructorId",
                table: "Trainings");

            migrationBuilder.DropColumn(
                name: "InternalInstructorId",
                table: "Trainings");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "Trainings");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "Trainings");

            migrationBuilder.DropColumn(
                name: "MaximumParticipants",
                table: "Trainings");

            migrationBuilder.DropColumn(
                name: "StartTimeTicks",
                table: "Trainings");

            // Geen datum-naar-eigenaarconversie: de guard heeft de lege oude tabel bewezen.
            migrationBuilder.DropColumn(name: "Date", table: "Trainings");
            migrationBuilder.AddColumn<string>(
                name: "InstructorUserId", table: "Trainings", type: "TEXT", nullable: false, defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MaximumCapacity = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                    table.CheckConstraint("CK_Location_Address", "Address IS NULL OR (typeof(Address) = 'text' AND length(Address) <= 500 AND instr(Address,char(0)) = 0)");
                    table.CheckConstraint("CK_Location_Capacity", "typeof(MaximumCapacity) = 'integer' AND MaximumCapacity > 0");
                    table.CheckConstraint("CK_Location_Name", "typeof(Name) = 'text' AND length(Name) <= 200 AND length(trim(Name)) >= 1 AND instr(Name,char(0)) = 0");
                    table.CheckConstraint("CK_Location_Version", "typeof(Version) = 'text' AND length(Version) = 36 AND instr(Version,char(0)) = 0\nAND Version <> '00000000-0000-0000-0000-000000000000'\nAND substr(Version,9,1) = '-' AND substr(Version,14,1) = '-'\nAND substr(Version,19,1) = '-' AND substr(Version,24,1) = '-'\nAND length(replace(Version,'-','')) = 32\nAND replace(Version,'-','') NOT GLOB '*[^0-9A-Fa-f]*'");
                });

            migrationBuilder.CreateTable(
                name: "WaitlistEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrainingId = table.Column<int>(type: "INTEGER", nullable: false),
                    ApplicationUserId = table.Column<string>(type: "TEXT", nullable: false),
                    Motivation = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    JoinedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    ClosedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaitlistEntries", x => x.Id);
                    table.CheckConstraint("CK_WaitlistEntry_ClosedAtUtc", "ClosedAtUtc IS NULL OR (typeof(ClosedAtUtc) = 'text' AND length(ClosedAtUtc) = 28 AND instr(ClosedAtUtc,char(0)) = 0 AND ClosedAtUtc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z')");
                    table.CheckConstraint("CK_WaitlistEntry_JoinedAtUtc", "typeof(JoinedAtUtc) = 'text' AND length(JoinedAtUtc) = 28 AND instr(JoinedAtUtc,char(0)) = 0 AND JoinedAtUtc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z'");
                    table.CheckConstraint("CK_WaitlistEntry_Motivation", "Motivation IS NULL OR (typeof(Motivation) = 'text' AND length(Motivation) <= 4000 AND instr(Motivation,char(0)) = 0)");
                    table.CheckConstraint("CK_WaitlistEntry_Version", "typeof(Version) = 'text' AND length(Version) = 36 AND instr(Version,char(0)) = 0\nAND Version <> '00000000-0000-0000-0000-000000000000'\nAND substr(Version,9,1) = '-' AND substr(Version,14,1) = '-'\nAND substr(Version,19,1) = '-' AND substr(Version,24,1) = '-'\nAND length(replace(Version,'-','')) = 32\nAND replace(Version,'-','') NOT GLOB '*[^0-9A-Fa-f]*'");
                    table.ForeignKey(
                        name: "FK_WaitlistEntries_AspNetUsers_ApplicationUserId",
                        column: x => x.ApplicationUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WaitlistEntries_Trainings_TrainingId",
                        column: x => x.TrainingId,
                        principalTable: "Trainings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TrainingMoments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrainingId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<string>(type: "TEXT", nullable: false),
                    StartTimeTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTimeTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    LocationId = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumParticipants = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingMoments", x => x.Id);
                    table.CheckConstraint("CK_TrainingMoment_Capacity", "typeof(MaximumParticipants) = 'integer' AND MaximumParticipants > 0");
                    table.CheckConstraint("CK_TrainingMoment_Date", "typeof(Date) = 'text' AND length(Date) = 10 AND instr(Date,char(0)) = 0 AND Date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'");
                    table.CheckConstraint("CK_TrainingMoment_Status", "typeof(Status) = 'integer' AND Status IN (1,2)");
                    table.CheckConstraint("CK_TrainingMoment_Time", "typeof(StartTimeTicks) = 'integer' AND typeof(EndTimeTicks) = 'integer' AND StartTimeTicks BETWEEN 0 AND 863999999999 AND EndTimeTicks BETWEEN 0 AND 863999999999 AND EndTimeTicks > StartTimeTicks");
                    table.CheckConstraint("CK_TrainingMoment_Version", "typeof(Version) = 'text' AND length(Version) = 36 AND instr(Version,char(0)) = 0\nAND Version <> '00000000-0000-0000-0000-000000000000'\nAND substr(Version,9,1) = '-' AND substr(Version,14,1) = '-'\nAND substr(Version,19,1) = '-' AND substr(Version,24,1) = '-'\nAND length(replace(Version,'-','')) = 32\nAND replace(Version,'-','') NOT GLOB '*[^0-9A-Fa-f]*'");
                    table.ForeignKey(
                        name: "FK_TrainingMoments_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrainingMoments_Trainings_TrainingId",
                        column: x => x.TrainingId,
                        principalTable: "Trainings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Registrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TrainingMomentId = table.Column<int>(type: "INTEGER", nullable: false),
                    ApplicationUserId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Motivation = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    RequestedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false),
                    DecisionReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registrations", x => x.Id);
                    table.CheckConstraint("CK_Registration_DecisionReason", "DecisionReason IS NULL OR (typeof(DecisionReason) = 'text' AND length(DecisionReason) <= 1000 AND instr(DecisionReason,char(0)) = 0)");
                    table.CheckConstraint("CK_Registration_Motivation", "Motivation IS NULL OR (typeof(Motivation) = 'text' AND length(Motivation) <= 4000 AND instr(Motivation,char(0)) = 0)");
                    table.CheckConstraint("CK_Registration_RequestedAtUtc", "typeof(RequestedAtUtc) = 'text' AND length(RequestedAtUtc) = 28 AND instr(RequestedAtUtc,char(0)) = 0 AND RequestedAtUtc GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z'");
                    table.CheckConstraint("CK_Registration_Status", "typeof(Status) = 'integer' AND Status IN (0,1,2,3)");
                    table.CheckConstraint("CK_Registration_Version", "typeof(Version) = 'text' AND length(Version) = 36 AND instr(Version,char(0)) = 0\nAND Version <> '00000000-0000-0000-0000-000000000000'\nAND substr(Version,9,1) = '-' AND substr(Version,14,1) = '-'\nAND substr(Version,19,1) = '-' AND substr(Version,24,1) = '-'\nAND length(replace(Version,'-','')) = 32\nAND replace(Version,'-','') NOT GLOB '*[^0-9A-Fa-f]*'");
                    table.ForeignKey(
                        name: "FK_Registrations_AspNetUsers_ApplicationUserId",
                        column: x => x.ApplicationUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Registrations_TrainingMoments_TrainingMomentId",
                        column: x => x.TrainingMomentId,
                        principalTable: "TrainingMoments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Trainings_InstructorUserId",
                table: "Trainings",
                column: "InstructorUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Training_Booleans",
                table: "Trainings",
                sql: "typeof(RequiresMotivation) = 'integer' AND RequiresMotivation IN (0,1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Training_Description",
                table: "Trainings",
                sql: "Description IS NULL OR (typeof(Description) = 'text' AND length(Description) <= 4000 AND instr(Description,char(0)) = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Training_Instructor",
                table: "Trainings",
                sql: "typeof(InstructorUserId) = 'text' AND length(trim(InstructorUserId)) >= 1 AND instr(InstructorUserId,char(0)) = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Training_Price",
                table: "Trainings",
                sql: "ExternalTotalPriceCents IS NULL OR (typeof(ExternalTotalPriceCents) = 'integer' AND ExternalTotalPriceCents >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Training_Version",
                table: "Trainings",
                sql: "typeof(Version) = 'text' AND length(Version) = 36 AND instr(Version,char(0)) = 0\nAND Version <> '00000000-0000-0000-0000-000000000000'\nAND substr(Version,9,1) = '-' AND substr(Version,14,1) = '-'\nAND substr(Version,19,1) = '-' AND substr(Version,24,1) = '-'\nAND length(replace(Version,'-','')) = 32\nAND replace(Version,'-','') NOT GLOB '*[^0-9A-Fa-f]*'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Category_Version",
                table: "Categories",
                sql: "typeof(Version) = 'text' AND length(Version) = 36 AND instr(Version,char(0)) = 0\nAND Version <> '00000000-0000-0000-0000-000000000000'\nAND substr(Version,9,1) = '-' AND substr(Version,14,1) = '-'\nAND substr(Version,19,1) = '-' AND substr(Version,24,1) = '-'\nAND length(replace(Version,'-','')) = 32\nAND replace(Version,'-','') NOT GLOB '*[^0-9A-Fa-f]*'");

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_ApplicationUserId",
                table: "Registrations",
                column: "ApplicationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_TrainingMomentId_ApplicationUserId",
                table: "Registrations",
                columns: new[] { "TrainingMomentId", "ApplicationUserId" },
                unique: true,
                filter: "Status IN (0,1)");

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_TrainingMomentId_Status",
                table: "Registrations",
                columns: new[] { "TrainingMomentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TrainingMoments_LocationId_Date_Status_StartTimeTicks",
                table: "TrainingMoments",
                columns: new[] { "LocationId", "Date", "Status", "StartTimeTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_TrainingMoments_TrainingId",
                table: "TrainingMoments",
                column: "TrainingId");

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_ApplicationUserId",
                table: "WaitlistEntries",
                column: "ApplicationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_TrainingId_ApplicationUserId",
                table: "WaitlistEntries",
                columns: new[] { "TrainingId", "ApplicationUserId" },
                unique: true,
                filter: "ClosedAtUtc IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEntries_TrainingId_JoinedAtUtc_Id",
                table: "WaitlistEntries",
                columns: new[] { "TrainingId", "JoinedAtUtc", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_Trainings_AspNetUsers_InstructorUserId",
                table: "Trainings",
                column: "InstructorUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Een terugweg verliest het herziene opleidings-/momentmodel; geen destructieve downgrade uitvoeren.
            throw new NotSupportedException("Een downgrade van sprint 003 revisie 02 wordt niet ondersteund.");
        }
    }
}
