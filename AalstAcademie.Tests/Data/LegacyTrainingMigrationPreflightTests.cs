// Sprint 003: Controleert het oude fysieke schema vóór de migratie
// Bestaande oude opleidingsrijen moeten de upgrade blokkeren zonder verzonnen conversie.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Data;

/// <summary>De preflight leest het fysieke oude schema zonder EF tegen verdwenen opleidingsvelden te laten queryen.</summary>
public class LegacyTrainingMigrationPreflightTests
{
    private const string OldReview = "20261002160213_AddAccountReview";
    private const string OldTraining = "20261003125215_AddTrainingManagement";

    [Fact]
    public async Task Fresh_database_is_read_without_schema_writes()
    {
        await using var db = await SqliteTestDatabase.CreateAsync(migrate: false);
        await LegacyTrainingMigrationPreflight.ValidateAsync(db.Context);
        Assert.Empty(await db.Context.Database.GetAppliedMigrationsAsync());
    }
    [Fact]
    public async Task Old_review_schema_is_supported_without_early_migration()
    {
        await using var db = await SqliteTestDatabase.CreateAsync(migrate: false);
        await db.Context.GetService<IMigrator>().MigrateAsync(OldReview);
        await LegacyTrainingMigrationPreflight.ValidateAsync(db.Context);
        Assert.Equal(3, (await db.Context.Database.GetAppliedMigrationsAsync()).Count());
        // Een onbekende extra tabel wordt niet als een ondersteunde lege legacyvariant behandeld.
        await db.Context.Database.ExecuteSqlRawAsync("CREATE TABLE OnbekendeLegacyVorm(Id INTEGER)");
        await Assert.ThrowsAsync<InvalidOperationException>(() => LegacyTrainingMigrationPreflight.ValidateAsync(db.Context));
        Assert.Equal(3, (await db.Context.Database.GetAppliedMigrationsAsync()).Count());
    }
    [Fact]
    public async Task Old_empty_training_schema_is_supported_without_early_migration()
    {
        await using var db = await SqliteTestDatabase.CreateAsync(migrate: false);
        await db.Context.GetService<IMigrator>().MigrateAsync(OldTraining);
        await LegacyTrainingMigrationPreflight.ValidateAsync(db.Context);
        Assert.Equal(4, (await db.Context.Database.GetAppliedMigrationsAsync()).Count());
    }
    [Fact]
    public async Task Old_nonempty_training_fails_before_durable_changes()
    {
        await using var db = await SqliteTestDatabase.CreateAsync(migrate: false);
        await db.Context.GetService<IMigrator>().MigrateAsync(OldTraining);
        // Raw SQL vult uitsluitend de eigen oude providerfixture; geen nieuwe CLR-shape of gebruikersdatabase.
        await db.Context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
        await db.Context.Database.ExecuteSqlRawAsync("""
            INSERT INTO Categories(Id,Name,NormalizedName,Version) VALUES(91,'Fictief','FICTIEF','11111111-1111-1111-1111-111111111111');
            INSERT INTO Trainings(Id,Title,CategoryId,Date,StartTimeTicks,EndTimeTicks,Location,MaximumParticipants,
                InternalInstructorId,AudienceScope,RequiresMotivation,IsArchived,Version)
                VALUES(92,'Fictieve legacy',91,'2026-12-15',0,1,'Fictief',1,99,0,0,0,'22222222-2222-2222-2222-222222222222');
            """);
        await db.Context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");
        var before = (await db.Context.Database.GetAppliedMigrationsAsync()).ToArray();
        var schema = await RawDigestAsync(db.Context, "SELECT type,name,sql FROM sqlite_master ORDER BY type,name");
        var rows = await RawDigestAsync(db.Context, "SELECT * FROM Trainings ORDER BY Id");
        var categories = await RawDigestAsync(db.Context, "SELECT * FROM Categories ORDER BY Id");
        // De echte initializer moet eveneens vóór migrations/rollen/afdelingen falen.
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync());
        Assert.Equal(before, await db.Context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(4, before.Length);
        Assert.Equal(schema, await RawDigestAsync(db.Context, "SELECT type,name,sql FROM sqlite_master ORDER BY type,name"));
        Assert.Equal(rows, await RawDigestAsync(db.Context, "SELECT * FROM Trainings ORDER BY Id"));
        Assert.Equal(categories, await RawDigestAsync(db.Context, "SELECT * FROM Categories ORDER BY Id"));
        Assert.Equal(0L, await CountAsync(db.Context, "SELECT count(*) FROM AspNetRoles"));
        Assert.Equal(0L, await CountAsync(db.Context, "SELECT count(*) FROM Departments"));
    }
    [Fact]
    public async Task New_nonempty_schema_is_preserved_by_ordinary_initializer_preflight()
    {
        await using var db = await FileSqliteTestDatabase.CreateAsync(configureServices: services => TrainingTestServices.Configure(services));
        await TrainingTestData.InitializeAsync(db.Services);
        var owner = await TrainingTestData.CreateOwnerAsync(db.Services);
        await TrainingTestData.CreateTrainingAsync(db.Services, owner);
        var before = await TrainingTestData.DigestAsync(db.Services);
        await db.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        Assert.Equal(before, await TrainingTestData.DigestAsync(db.Services));
        Assert.Equal(6, (await db.Context.Database.GetAppliedMigrationsAsync()).Count());
    }

    /// <summary>Onafhankelijke raw schema-/data-readback retourneert alleen een hash, nooit fixturecredentials.</summary>
    private static async Task<string> RawDigestAsync(ApplicationDbContext context, string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values.Select(value => value is DBNull ? null : value).ToArray());
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(rows))));
    }

    private static async Task<object?> CountAsync(ApplicationDbContext context, string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
