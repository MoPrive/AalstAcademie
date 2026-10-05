using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AalstAcademie.Tests.Data;
/// <summary>Raw oude schemas zijn geen nieuwe EF-entities. Alleen eigen filefixtures worden gemigreerd of gehasht.</summary>
public class AccountManagerMigrationTests
{
    [Theory]
    [InlineData("FreshSixMigrations")]
    [InlineData("NullLegacyUpgradePreservesDigest")]
    [InlineData("EmptyLegacyUpgradePreservesDigest")]
    [InlineData("NonemptyPreflightRefusesBeforeWrites")]
    [InlineData("WhitespaceLegacyIsNonemptyAndRefused")]
    [InlineData("NonemptyDirectEfRefusesBeforeRebuild")]
    [InlineData("UnknownUserShapeRefused")]
    [InlineData("UnknownDepartmentShapeRefused")]
    [InlineData("UnknownMigrationHistoryRefused")]
    [InlineData("AlreadySixthShapeAccepted")]
    [InlineData("SixthHistoryWrongShapeRefused")]
    [InlineData("DownRefusedWithoutDataLoss")]
    [InlineData("ExistingFivePairsByteEqual")]
    [InlineData("ModelRelationsProviderReadback")]
    public async Task Guarded_sixth_migration(string variant)
    {
        await using var f = await FileSqliteTestDatabase.CreateAsync(migrate: false);
        var db = f.Context; await db.Database.OpenConnectionAsync(); var migrator = db.GetService<IMigrator>();
        var fresh = variant is "FreshSixMigrations" or "AlreadySixthShapeAccepted" or "DownRefusedWithoutDataLoss" or "ExistingFivePairsByteEqual" or "ModelRelationsProviderReadback";
        await migrator.MigrateAsync(fresh ? null : LegacyTrainingMigrationPreflight.RevisionMigration);
        if (fresh)
        {
            var before = await Digest(db, true);
            if (variant == "DownRefusedWithoutDataLoss")
            { await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync(LegacyTrainingMigrationPreflight.RevisionMigration)); Assert.Equal(before, await Digest(db, true)); }
            else await AccountManagerMigrationPreflight.ValidateAsync(db);
            Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
            if (variant == "ExistingFivePairsByteEqual")
            {
                // Vastgelegd vóór sprintcodewerk; ook whitespace/commentwijzigingen in oude migrations zijn verboden.
                var expected = new Dictionary<string,string>
                {
                    ["AalstAcademie.Web/Data/Migrations/00000000000000_CreateIdentitySchema.cs"] = "BA17A0339856DE13C0A0DAE4D90A14E99119136BAB2F055F431385F239BD4C1A",
                    ["AalstAcademie.Web/Data/Migrations/00000000000000_CreateIdentitySchema.Designer.cs"] = "BF706673B3BFC271BBFB287D008FB3AE5A60664C4378520759AC1A808D9348B4",
                    ["AalstAcademie.Web/Data/Migrations/20261002124853_AddAccountFoundation.cs"] = "69AB82F29B595671E5F512F00C55480D34300643DB8CF9C1E959BDECE9013C0C",
                    ["AalstAcademie.Web/Data/Migrations/20261002124853_AddAccountFoundation.Designer.cs"] = "FC511F1A4A92CB0A344CF76CBA03BA070E9AFAA34F93114F1308F56167FC4558",
                    ["AalstAcademie.Web/Data/Migrations/20261002160213_AddAccountReview.cs"] = "2954FB56EDA74ED81A910927312C099E39AE5C2BCAAEAE89B96E5FBF1C10878E",
                    ["AalstAcademie.Web/Data/Migrations/20261002160213_AddAccountReview.Designer.cs"] = "0F04552A7FA938B45F6667B60ACAB6720FEA34CC85B4013AC7C111CA05DFB0EA",
                    ["AalstAcademie.Web/Data/Migrations/20261003125215_AddTrainingManagement.cs"] = "F16B177C05D9A13BE18FF41DC034C876463DF2EB80650A937B6892C7981610CE",
                    ["AalstAcademie.Web/Data/Migrations/20261003125215_AddTrainingManagement.Designer.cs"] = "3DA568F05E81EE0D8E3DE8BC8166D0BBF398750716571D87D36F3EF09304E530",
                    ["AalstAcademie.Web/Data/Migrations/20261004000000_SplitTrainingDefinitionsAndMoments.cs"] = "2AB52D7AF2F8810D59DE20CB5E9ECC22E4488479C1E0F9BBEB02B00FB847A258",
                    ["AalstAcademie.Web/Data/Migrations/20261004000000_SplitTrainingDefinitionsAndMoments.Designer.cs"] = "FA77DF302A2C41C9DC6B6DBE8DE31F9F25ABDF7C9EF7BA35F628E026B6E92F4D",
                };
                var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath())!, "..", ".."));
                foreach (var pair in expected) Assert.Equal(pair.Value, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, pair.Key)))));
            }
            if (variant == "ModelRelationsProviderReadback")
            {
                Assert.Equal(DeleteBehavior.Restrict, db.Model.FindEntityType(typeof(ApplicationUser))!.GetForeignKeys().Single(x => x.Properties.Single().Name == "ManagerUserId").DeleteBehavior);
                Assert.True(db.Model.FindEntityType(typeof(Department))!.FindProperty("ResponsibleUserId")!.IsConcurrencyToken);
                Assert.Contains("ManagerUserId", await Strings(db, "SELECT [from] FROM pragma_foreign_key_list('AspNetUsers') WHERE on_delete='RESTRICT'"));
                Assert.Contains("ResponsibleUserId", await Strings(db, "SELECT [from] FROM pragma_foreign_key_list('Departments') WHERE on_delete='RESTRICT'"));
            }
            return;
        }
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Departments(Id,Name) VALUES(91,'Fictieve migratieafdeling');
            INSERT INTO AspNetUsers(Id,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount,
                AccountApprovalStatus,IsBlocked,FirstName,LastName,DepartmentId,SecurityStamp,ConcurrencyStamp,PasswordHash)
            VALUES('legacy',0,0,0,0,0,0,0,'Noor','Demo',91,'fictieve-security','fictieve-versie','fictieve-hash');
            """);
        if (variant is "EmptyLegacyUpgradePreservesDigest") await db.Database.ExecuteSqlRawAsync("UPDATE AspNetUsers SET ManagerName='' WHERE Id='legacy'");
        if (variant is "NonemptyPreflightRefusesBeforeWrites" or "NonemptyDirectEfRefusesBeforeRebuild" or "WhitespaceLegacyIsNonemptyAndRefused")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AspNetUsers SET ManagerName={(variant == "WhitespaceLegacyIsNonemptyAndRefused" ? "  " : "Bestaande informatie")} WHERE Id='legacy'");
        if (variant == "UnknownUserShapeRefused") await db.Database.ExecuteSqlRawAsync("ALTER TABLE AspNetUsers ADD COLUMN UnknownField TEXT");
        if (variant == "UnknownDepartmentShapeRefused") await db.Database.ExecuteSqlRawAsync("ALTER TABLE Departments ADD COLUMN UnknownField TEXT");
        if (variant is "UnknownMigrationHistoryRefused" or "SixthHistoryWrongShapeRefused")
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO __EFMigrationsHistory(MigrationId,ProductVersion) VALUES({(variant == "UnknownMigrationHistoryRefused" ? "99999999999999_Unknown" : AccountManagerMigrationPreflight.MigrationId)},'10.0.0')");
        var durableBefore = await Digest(db, true);
        if (variant is "NullLegacyUpgradePreservesDigest" or "EmptyLegacyUpgradePreservesDigest")
        {
            var before = await Digest(db, false);
            await AccountManagerMigrationPreflight.ValidateAsync(db); await migrator.MigrateAsync();
            Assert.Equal(before, await Digest(db, false));
            Assert.Null((await db.Users.AsNoTracking().SingleAsync()).ManagerUserId);
            Assert.Null((await db.Departments.AsNoTracking().SingleAsync()).ResponsibleUserId);
            Assert.DoesNotContain("ManagerName", await Strings(db, "SELECT name FROM pragma_table_info('AspNetUsers')"));
        }
        else
        {
            if (variant == "NonemptyDirectEfRefusesBeforeRebuild") await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync());
            else await Assert.ThrowsAsync<InvalidOperationException>(() => AccountManagerMigrationPreflight.ValidateAsync(db));
            Assert.Equal(durableBefore, await Digest(db, true));
        }
    }
    private static string SourcePath([CallerFilePath] string path = "") => path;
    private static async Task<string[]> Strings(ApplicationDbContext db, string sql)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand(); cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(); var values = new List<string>();
        while(await reader.ReadAsync()) values.Add(reader.GetString(0)); return values.ToArray();
    }
    private static async Task<string> Digest(ApplicationDbContext db, bool schema)
    {
        var parts = new List<string>();
        if (schema) parts.Add(await Raw(db, "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name<>'__EFMigrationsLock' ORDER BY type,name"));
        var tables = await Strings(db, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'__EFMigrationsLock' ORDER BY name");
        foreach(var table in tables)
        {
            if (!schema && table == "__EFMigrationsHistory") continue;
            var names = await Strings(db, $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid");
            if (!schema) names = names.Where(x => x is not "ManagerName" and not "ManagerUserId" and not "ResponsibleUserId").Order(StringComparer.Ordinal).ToArray();
            parts.Add(await Raw(db, "SELECT " + string.Join(',', names.Select(x => "\"" + x.Replace("\"","\"\"") + "\"")) + " FROM \"" + table + "\" ORDER BY rowid"));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',parts))));
    }
    private static async Task<string> Raw(ApplicationDbContext db, string sql)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand(); cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(); var rows = new List<object[]>();
        while(await reader.ReadAsync()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row.Select(x => x is DBNull ? null! : x).ToArray()); }
        return JsonSerializer.Serialize(rows);
    }
}
