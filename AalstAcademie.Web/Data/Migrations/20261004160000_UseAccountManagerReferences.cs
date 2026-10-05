using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AalstAcademie.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class UseAccountManagerReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sprint 004: eerste operatie weigert verlies van oude naamtekst, onbekende kolommen of history.
            // Een tijdelijke CHECK-guard is transactioneel en wordt geen nieuwe domein-/seedtabel.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE __Sprint004ManagerGuard
                  (Allowed INTEGER NOT NULL CONSTRAINT CK_Sprint004_RequiresSafeManagerUpgrade CHECK(Allowed=1));
                INSERT INTO __Sprint004ManagerGuard(Allowed)
                SELECT CASE WHEN
                  (SELECT count(*) FROM main.__EFMigrationsHistory)=5 AND
                  NOT EXISTS(SELECT 1 FROM main.__EFMigrationsHistory WHERE MigrationId NOT IN ('00000000000000_CreateIdentitySchema','20261002124853_AddAccountFoundation','20261002160213_AddAccountReview','20261003125215_AddTrainingManagement','20261004000000_SplitTrainingDefinitionsAndMoments')) AND
                  (SELECT count(*) FROM pragma_table_info('AspNetUsers'))=27 AND
                  NOT EXISTS(SELECT name,type FROM pragma_table_info('AspNetUsers') EXCEPT SELECT column1,column2 FROM (VALUES ('AccessFailedCount','INTEGER'),('AccountApprovalStatus','INTEGER'),('AccountRequestedAtUtc','TEXT'),('ConcurrencyStamp','TEXT'),('DemoSeedKey','TEXT'),('DepartmentId','INTEGER'),('Email','TEXT'),('EmailConfirmed','INTEGER'),('FirstName','TEXT'),('Id','TEXT'),('IsBlocked','INTEGER'),('LastName','TEXT'),('LockoutEnabled','INTEGER'),('LockoutEnd','TEXT'),('ManagerName','TEXT'),('NormalizedEmail','TEXT'),('NormalizedUserName','TEXT'),('PasswordHash','TEXT'),('PhoneNumber','TEXT'),('PhoneNumberConfirmed','INTEGER'),('RefusalReason','TEXT'),('RequestedAccountType','INTEGER'),('ReviewedAt','TEXT'),('ReviewedById','TEXT'),('SecurityStamp','TEXT'),('TwoFactorEnabled','INTEGER'),('UserName','TEXT'))) AND
                  (SELECT count(*) FROM pragma_table_info('Departments'))=2 AND
                  NOT EXISTS(SELECT name,type FROM pragma_table_info('Departments') EXCEPT SELECT column1,column2 FROM (VALUES ('Id','INTEGER'),('Name','TEXT'))) AND
                  NOT EXISTS(SELECT 1 FROM main.AspNetUsers WHERE ManagerName IS NOT NULL AND ManagerName <> '')
                THEN 1 ELSE 0 END;
                DROP TABLE temp.__Sprint004ManagerGuard;
                """, suppressTransaction: false);

            migrationBuilder.DropColumn(
                name: "ManagerName",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<string>(
                name: "ResponsibleUserId",
                table: "Departments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManagerUserId",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Departments_ResponsibleUserId",
                table: "Departments",
                column: "ResponsibleUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_ManagerUserId",
                table: "AspNetUsers",
                column: "ManagerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_AspNetUsers_ManagerUserId",
                table: "AspNetUsers",
                column: "ManagerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Departments_AspNetUsers_ResponsibleUserId",
                table: "Departments",
                column: "ResponsibleUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Een terugweg zou de accountreferenties verliezen; hiervoor is geen destructieve conversie goedgekeurd.
            throw new System.NotSupportedException("Een downgrade van sprint 004 wordt niet ondersteund.");
        }
    }
}
