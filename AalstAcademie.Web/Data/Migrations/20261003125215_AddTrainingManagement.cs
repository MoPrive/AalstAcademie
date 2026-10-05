using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AalstAcademie.Web.Data.Migrations
{
    // Sprint003: uitsluitend drie beheertabellen met integriteitsregels en indexen, zonder featureseed.
    // Bestaande Identity-/profieltabellen en hun data blijven buiten deze migratie.
    /// <inheritdoc />
    public partial class AddTrainingManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "BINARY"),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.CheckConstraint("CK_Category_Name", "typeof(Name) = 'text' AND length(Name) <= 100 AND length(trim(Name)) >= 1 AND instr(Name,char(0)) = 0");
                    table.CheckConstraint("CK_Category_NormalizedName", "typeof(NormalizedName) = 'text' AND length(NormalizedName) <= 100 AND length(trim(NormalizedName)) >= 1 AND instr(NormalizedName,char(0)) = 0");
                    table.CheckConstraint("CK_Category_Version", "typeof(Version) = 'text' AND length(Version) = 36\nAND Version <> '00000000-0000-0000-0000-000000000000'\nAND substr(Version,9,1) = '-' AND substr(Version,14,1) = '-'\nAND substr(Version,19,1) = '-' AND substr(Version,24,1) = '-'\nAND length(replace(Version,'-','')) = 32\nAND replace(Version,'-','') NOT GLOB '*[^0-9A-Fa-f]*'");
                });

            migrationBuilder.CreateTable(
                name: "Trainings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<string>(type: "TEXT", nullable: false),
                    StartTimeTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    EndTimeTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    MaximumParticipants = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalTotalPriceCents = table.Column<long>(type: "INTEGER", nullable: true),
                    InternalInstructorId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExternalInstructorId = table.Column<int>(type: "INTEGER", nullable: true),
                    AudienceScope = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    RequiresMotivation = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trainings", x => x.Id);
                    table.CheckConstraint("CK_Training_Audience", "typeof(AudienceScope) = 'integer' AND AudienceScope IN (0,1,2)");
                    table.CheckConstraint("CK_Training_Booleans", "typeof(IsArchived) = 'integer' AND IsArchived IN (0,1) AND typeof(RequiresMotivation) = 'integer' AND RequiresMotivation IN (0,1)");
                    table.CheckConstraint("CK_Training_Capacity", "typeof(MaximumParticipants) = 'integer' AND MaximumParticipants > 0");
                    table.CheckConstraint("CK_Training_Date", "typeof(Date) = 'text' AND length(Date) = 10 AND Date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'");
                    table.CheckConstraint("CK_Training_Description", "Description IS NULL OR (typeof(Description) = 'text' AND length(Description) <= 4000)");
                    table.CheckConstraint("CK_Training_InstructorXor", "(InternalInstructorId IS NOT NULL AND ExternalInstructorId IS NULL) OR (InternalInstructorId IS NULL AND ExternalInstructorId IS NOT NULL)");
                    table.CheckConstraint("CK_Training_Location", "typeof(Location) = 'text' AND length(Location) <= 200 AND length(trim(Location)) >= 1 AND instr(Location,char(0)) = 0");
                    table.CheckConstraint("CK_Training_Price", "(InternalInstructorId IS NOT NULL AND ExternalTotalPriceCents IS NULL) OR (ExternalInstructorId IS NOT NULL AND ExternalTotalPriceCents IS NOT NULL AND typeof(ExternalTotalPriceCents) = 'integer' AND ExternalTotalPriceCents >= 0)");
                    table.CheckConstraint("CK_Training_Time", "typeof(StartTimeTicks) = 'integer' AND typeof(EndTimeTicks) = 'integer' AND StartTimeTicks BETWEEN 0 AND 863999999999 AND EndTimeTicks BETWEEN 0 AND 863999999999 AND EndTimeTicks > StartTimeTicks");
                    table.CheckConstraint("CK_Training_Title", "typeof(Title) = 'text' AND length(Title) <= 200 AND length(trim(Title)) >= 1 AND instr(Title,char(0)) = 0");
                    table.CheckConstraint("CK_Training_Version", "typeof(Version) = 'text' AND length(Version) = 36\nAND Version <> '00000000-0000-0000-0000-000000000000'\nAND substr(Version,9,1) = '-' AND substr(Version,14,1) = '-'\nAND substr(Version,19,1) = '-' AND substr(Version,24,1) = '-'\nAND length(replace(Version,'-','')) = 32\nAND replace(Version,'-','') NOT GLOB '*[^0-9A-Fa-f]*'");
                    table.ForeignKey(
                        name: "FK_Trainings_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Trainings_ExternalInstructors_ExternalInstructorId",
                        column: x => x.ExternalInstructorId,
                        principalTable: "ExternalInstructors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Trainings_InternalInstructors_InternalInstructorId",
                        column: x => x.InternalInstructorId,
                        principalTable: "InternalInstructors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TrainingDepartments",
                columns: table => new
                {
                    TrainingId = table.Column<int>(type: "INTEGER", nullable: false),
                    DepartmentId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingDepartments", x => new { x.TrainingId, x.DepartmentId });
                    table.ForeignKey(
                        name: "FK_TrainingDepartments_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrainingDepartments_Trainings_TrainingId",
                        column: x => x.TrainingId,
                        principalTable: "Trainings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_NormalizedName",
                table: "Categories",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrainingDepartments_DepartmentId",
                table: "TrainingDepartments",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Trainings_CategoryId",
                table: "Trainings",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Trainings_ExternalInstructorId",
                table: "Trainings",
                column: "ExternalInstructorId");

            migrationBuilder.CreateIndex(
                name: "IX_Trainings_InternalInstructorId",
                table: "Trainings",
                column: "InternalInstructorId");

            migrationBuilder.CreateIndex(
                name: "IX_Trainings_IsArchived_Date_StartTimeTicks_Id",
                table: "Trainings",
                columns: new[] { "IsArchived", "Date", "StartTimeTicks", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Technische schemarollback: eerst child dan parents; dit is geen applicatie-deletefunctie.
            migrationBuilder.DropTable(
                name: "TrainingDepartments");

            migrationBuilder.DropTable(
                name: "Trainings");

            migrationBuilder.DropTable(
                name: "Categories");
        }
    }
}
