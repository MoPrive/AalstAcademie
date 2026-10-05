// Sprint 003: Bewijst behoud van accounts en categorieën bij een lege oude opleidingstabel, weigering van legacyopleidingsdata en het uiteindelijke schema met zeventien tabellen.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AalstAcademie.Tests.Data;

/// <summary>
/// Bewijst behoud bij lege legacyopleidingen, afwijzing van oude data en het nieuwe zeventien-tabellenschema.
/// De oude accounts en credentials zijn volledig fictief; de gebruikersdatabase wordt nooit gekopieerd.
/// </summary>
public class TrainingUpgradeTests
{
    private static readonly string[] PreviousMigrations =
    [
        "00000000000000_CreateIdentitySchema",
        "20261002124853_AddAccountFoundation",
        "20261002160213_AddAccountReview"
    ];

    // U1: IMigrator houdt de fixture eerst op exact drie migraties. Een initializer zou die grens overslaan.
    [Fact]
    public async Task Empty_legacy_training_upgrade_preserves_accounts_categories_and_ids()
    {
        await using var db = await SqliteTestDatabase.CreateAsync(migrate: false);
        await db.Context.GetService<IMigrator>().MigrateAsync(PreviousMigrations[^1]);
        Assert.Equal(PreviousMigrations, await db.Context.Database.GetAppliedMigrationsAsync());
        await db.Context.Database.ExecuteSqlRawAsync("""
            INSERT INTO Departments (Id, Name) VALUES (91, 'Fictieve legacy-afdeling'), (92, 'Fictieve tweede afdeling');
            INSERT INTO AspNetRoles (Id, Name, NormalizedName, ConcurrencyStamp) VALUES
                ('legacy-admin-role', 'Beheerder', 'BEHEERDER', 'fictieve-admin-roleversie'),
                ('legacy-staff-role', 'Medewerker', 'MEDEWERKER', 'fictieve-staff-roleversie'),
                ('legacy-instructor-role', 'Lesgever', 'LESGEVER', 'fictieve-instructor-roleversie');
            INSERT INTO AspNetUsers (Id, UserName, NormalizedUserName, Email, NormalizedEmail,
                PasswordHash, SecurityStamp, ConcurrencyStamp, EmailConfirmed, PhoneNumber, PhoneNumberConfirmed,
                TwoFactorEnabled, LockoutEnabled, AccessFailedCount, RequestedAccountType,
                AccountApprovalStatus, IsBlocked, FirstName, LastName, DepartmentId, ManagerName,
                AccountRequestedAtUtc, ReviewedById, ReviewedAt, RefusalReason, DemoSeedKey) VALUES
                ('legacy-admin', 'legacy-admin@example.invalid', 'LEGACY-ADMIN@EXAMPLE.INVALID',
                 'legacy-admin@example.invalid', 'LEGACY-ADMIN@EXAMPLE.INVALID', 'fictieve-adminhash',
                 'fictieve-adminstamp', 'fictieve-adminversie', 1, '0490000000', 0, 0, 1, 0,
                 NULL, 1, 0, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'fictieve.legacy.admin'),
                ('legacy-internal', 'legacy-internal@example.invalid', 'LEGACY-INTERNAL@EXAMPLE.INVALID',
                 'legacy-internal@example.invalid', 'LEGACY-INTERNAL@EXAMPLE.INVALID', 'fictieve-internehash',
                 'fictieve-internestamp', 'fictieve-interneversie', 0, '0490000001', 1, 1, 1, 2,
                 2, 1, 0, 'Noor', 'Fictief', 91, NULL, '2026-10-01 08:30:00', 'legacy-admin',
                 '2026-10-02 12:00:00+00:00', NULL, 'fictieve.legacy.internal'),
                ('legacy-external', 'legacy-external@example.invalid', 'LEGACY-EXTERNAL@EXAMPLE.INVALID',
                 'legacy-external@example.invalid', 'LEGACY-EXTERNAL@EXAMPLE.INVALID', 'fictieve-externehash',
                 'fictieve-externestamp', 'fictieve-externeversie', 0, '0490000002', 0, 0, 0, 0,
                 3, 2, 1, NULL, NULL, NULL, NULL, '2026-10-01 09:30:00', 'legacy-admin',
                 '2026-10-02 13:00:00+00:00', 'Fictieve weigeringreden', NULL);
            INSERT INTO InternalInstructors (Id, ApplicationUserId) VALUES (31, 'legacy-internal');
            INSERT INTO ExternalInstructors (Id, ApplicationUserId, OrganizationName, VatNumber)
                VALUES (41, 'legacy-external', 'Fictieve legacyorganisatie', 'BE0000000000');
            INSERT INTO AspNetUserRoles (UserId, RoleId) VALUES
                ('legacy-admin', 'legacy-admin-role'),
                ('legacy-internal', 'legacy-staff-role'),
                ('legacy-internal', 'legacy-instructor-role');
            INSERT INTO AspNetUserClaims (Id, UserId, ClaimType, ClaimValue)
                VALUES (51, 'legacy-internal', 'fictieve-claim', 'fictieve-claimwaarde');
            INSERT INTO AspNetRoleClaims (Id, RoleId, ClaimType, ClaimValue)
                VALUES (61, 'legacy-instructor-role', 'fictieve-rolclaim', 'fictieve-rolclaimwaarde');
            INSERT INTO AspNetUserLogins (LoginProvider, ProviderKey, ProviderDisplayName, UserId)
                VALUES ('FictieveProvider', 'fictieve-sleutel', 'Fictieve provider', 'legacy-internal');
            INSERT INTO AspNetUserTokens (UserId, LoginProvider, Name, Value)
                VALUES ('legacy-internal', 'FictieveProvider', 'fictieve-tokennaam', 'fictieve-tokenwaarde');
            """);
        var before = await AccountDigestAsync(db.Context);

        // Eerst de historische vierde migratie, dan een fictieve categorie met bewust vaste sleutel.
        await db.Context.GetService<IMigrator>().MigrateAsync("20261003125215_AddTrainingManagement");
        await db.Context.Database.ExecuteSqlRawAsync("""
            INSERT INTO Categories (Id,Name,NormalizedName,Version)
            VALUES (81,'Fictieve legacycategorie','FICTIEVE LEGACYCATEGORIE','12345678-1234-1234-1234-123456789abc')
            """);
        var categoriesBefore = await RawDigestAsync(db.Context, "SELECT * FROM Categories ORDER BY Id");
        await db.Context.Database.MigrateAsync();
        db.Context.ChangeTracker.Clear();
        Assert.Equal(categoriesBefore, await RawDigestAsync(db.Context, "SELECT * FROM Categories ORDER BY Id"));
        Assert.Equal(before, await AccountDigestAsync(db.Context));
        var migrations = (await db.Context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(6, migrations.Length);
        Assert.Equal(PreviousMigrations, migrations[..3]);
        Assert.EndsWith("_AddTrainingManagement", migrations[3]);
        Assert.Equal(LegacyTrainingMigrationPreflight.RevisionMigration, migrations[4]);
        Assert.Equal(81, (await db.Context.Categories.AsNoTracking().SingleAsync()).Id);
        Assert.Empty(await db.Context.Trainings.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Context.TrainingDepartments.AsNoTracking().ToListAsync());
        // De geldige oude interne lesgever houdt zijn nullable leidinggevende en bestaande rechten.
        Assert.Null((await db.Context.Users.AsNoTracking().SingleAsync(x => x.Id == "legacy-internal")).ManagerUserId);
        Assert.Equal(3, await db.Context.UserRoles.CountAsync());
    }

    // U2: gewone migratie creëert zeven featuretabellen bovenop tien accounttabellen, zonder aanbodseed.
    [Fact]
    public async Task Fresh_migrations_create_seventeen_domain_tables_and_explicit_EF_metadata()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var migrations = (await db.Context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(6, migrations.Length);
        Assert.Equal(PreviousMigrations, migrations[..3]);
        Assert.EndsWith("_AddTrainingManagement", migrations[3]);
        Assert.Equal(LegacyTrainingMigrationPreflight.RevisionMigration, migrations[4]);
        var tables = await ReadStringsAsync(db.Context, """
            SELECT name FROM sqlite_master WHERE type = 'table'
                AND name NOT LIKE 'sqlite_%' ORDER BY name
            """);
        // EF-history en EF-lock zijn providerinfrastructuur en worden afzonderlijk van het domein benoemd.
        string[] expectedTables =
        [
            "AspNetRoleClaims", "AspNetRoles", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserRoles",
            "AspNetUserTokens", "AspNetUsers", "Categories", "Departments", "ExternalInstructors",
            "InternalInstructors", "Locations", "Registrations", "TrainingDepartments", "TrainingMoments",
            "Trainings", "WaitlistEntries", "__EFMigrationsHistory", "__EFMigrationsLock"
        ];
        Assert.Equal(expectedTables, tables);
        Assert.Equal(new[] { "TrainingId", "DepartmentId" }, await ReadStringsAsync(db.Context,
            "SELECT name FROM pragma_table_info('TrainingDepartments') ORDER BY cid"));
        Assert.Empty(await db.Context.Categories.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Context.Trainings.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Context.Users.AsNoTracking().ToListAsync());
    }


    // U3/U4: de eerste transactionele TEMP-guard stopt iedere actieve of gearchiveerde oude opleiding.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_migrator_rejects_any_legacy_training_without_durable_changes(bool archived)
    {
        await using var db = await SqliteTestDatabase.CreateAsync(migrate: false);
        var migrator = db.Context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261003125215_AddTrainingManagement");
        // Deze raw legacyfixture gebruikt uitsluitend het oude schema; geen nieuwe EF-entity kan dit maskeren.
        await db.Context.Database.ExecuteSqlRawAsync("""
            INSERT INTO Departments (Id,Name) VALUES (91,'Fictieve legacy-afdeling');
            INSERT INTO AspNetUsers (Id,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,
                AccessFailedCount,RequestedAccountType,AccountApprovalStatus,IsBlocked,FirstName,LastName,DepartmentId)
            VALUES ('old-owner',0,0,0,0,0,2,1,0,'Noor','Fictief',91);
            INSERT INTO InternalInstructors (Id,ApplicationUserId) VALUES (31,'old-owner');
            INSERT INTO Categories (Id,Name,NormalizedName,Version)
            VALUES (81,'Fictieve categorie','FICTIEVE CATEGORIE','12345678-1234-1234-1234-123456789abc');
            """);
        await db.Context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Trainings (Id,Title,Description,CategoryId,InternalInstructorId,ExternalInstructorId,
                Date,StartTimeTicks,EndTimeTicks,Location,MaximumParticipants,ExternalTotalPriceCents,
                AudienceScope,RequiresMotivation,IsArchived,Version)
            VALUES (71,'Fictieve oude opleiding',NULL,81,31,NULL,'2026-12-15',504000000000,
                576000000000,'Vrije oude locatie',12,NULL,0,0,{archived},
                '12345678-1234-1234-1234-123456789abc')
            """);
        var before = await DurableDigestAsync(db.Context);
        var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => migrator.MigrateAsync());
        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Equal(275, error.SqliteExtendedErrorCode);
        // Onafhankelijke providerreadbacks controleren schema, rijen en history; geen nieuwe IDregistratie.
        Assert.Equal(before, await DurableDigestAsync(db.Context));
        Assert.DoesNotContain(LegacyTrainingMigrationPreflight.RevisionMigration,
            await db.Context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(new[] { "InternalInstructorId", "ExternalInstructorId", "IsArchived" },
            await ReadStringsAsync(db.Context, """
                SELECT name FROM pragma_table_info('Trainings')
                WHERE name IN ('InternalInstructorId','ExternalInstructorId','IsArchived') ORDER BY cid
                """));
        Assert.Empty(await ReadStringsAsync(db.Context, "SELECT name FROM sqlite_master WHERE name='TrainingMoments'"));
    }

    // U5: inspecteer DownOperations zonder een echte downgrade of een dataverwijdering uit te voeren.
    [Fact]
    public async Task Revision_down_throws_before_creating_any_operations()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var assembly = db.Context.GetService<IMigrationsAssembly>();
        var type = assembly.Migrations[LegacyTrainingMigrationPreflight.RevisionMigration];
        var migration = assembly.CreateMigration(type, db.Context.Database.ProviderName!);
        Assert.Throws<NotSupportedException>(() => _ = migration.DownOperations);
    }

    /// <summary>Hash van schema en alle duurzame oude data; uitsluitend EF-lockrijen zijn vluchtige infrastructuur.</summary>
    private static async Task<string> DurableDigestAsync(ApplicationDbContext context)
    {
        var parts = new List<string> { await RawDigestAsync(context,
            "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE name <> '__EFMigrationsLock' ORDER BY type,name") };
        foreach (var table in await ReadStringsAsync(context,
            "SELECT name FROM sqlite_master WHERE type='table' AND name <> '__EFMigrationsLock' ORDER BY name"))
        {
            // Tabelnamen komen uit de eigen SQLite-catalogus; identifierescaping voorkomt interpretatie.
            var identifier = table.Replace("\"", "\"\"");
            parts.Add(await RawDigestAsync(context, $"SELECT * FROM \"{identifier}\" ORDER BY rowid"));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))));
    }

    /// <summary>Leest raw providerwaarden opnieuw en retourneert uitsluitend een digest, nooit credentials.</summary>
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
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows))));
    }

    /// <summary>
    /// Een digest van alle bestaande accounttabellen vergelijkt behoud zonder fictieve tokens/hashes
    /// in een assertionlog af te drukken. Alleen geordende, opnieuw uit SQLite gelezen waarden tellen mee.
    /// </summary>
    private static async Task<string> AccountDigestAsync(ApplicationDbContext context)
    {
        var snapshot = new
        {
            // De historische fixture wordt raw gelezen vóór de nieuwe EF-kolom bestaat.
            // Beide goedgekeurde nulvormen geven dezelfde readback; niet-lege tekst heeft eigen weigeringsproeven.
            Managers = await RawDigestAsync(context, (await ReadStringsAsync(context,
                "SELECT name FROM pragma_table_info('AspNetUsers')")).Contains("ManagerUserId")
                ? "SELECT Id, ManagerUserId FROM AspNetUsers ORDER BY Id"
                : "SELECT Id, CASE WHEN ManagerName='' THEN NULL ELSE ManagerName END FROM AspNetUsers ORDER BY Id"),
            Users = await context.Users.AsNoTracking().OrderBy(x => x.Id).Select(x => new
            {
                x.Id, x.UserName, x.NormalizedUserName, x.Email, x.NormalizedEmail, x.EmailConfirmed,
                x.PasswordHash, x.SecurityStamp, x.ConcurrencyStamp, x.PhoneNumber, x.PhoneNumberConfirmed,
                x.TwoFactorEnabled, x.LockoutEnd, x.LockoutEnabled, x.AccessFailedCount,
                x.RequestedAccountType, x.AccountApprovalStatus, x.IsBlocked, x.FirstName, x.LastName,
                x.DepartmentId, x.AccountRequestedAtUtc, x.ReviewedById, x.ReviewedAt,
                x.RefusalReason, x.DemoSeedKey
            }).ToArrayAsync(),
            Departments = await context.Departments.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.Name }).ToArrayAsync(),
            Internals = await context.InternalInstructors.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.ApplicationUserId }).ToArrayAsync(),
            Externals = await context.ExternalInstructors.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.ApplicationUserId, x.OrganizationName, x.VatNumber }).ToArrayAsync(),
            Roles = await context.Roles.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.Name, x.NormalizedName, x.ConcurrencyStamp }).ToArrayAsync(),
            Grants = await context.UserRoles.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.RoleId)
                .Select(x => new { x.UserId, x.RoleId }).ToArrayAsync(),
            Claims = await context.UserClaims.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.UserId, x.ClaimType, x.ClaimValue }).ToArrayAsync(),
            RoleClaims = await context.RoleClaims.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.RoleId, x.ClaimType, x.ClaimValue }).ToArrayAsync(),
            Logins = await context.UserLogins.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.LoginProvider).ThenBy(x => x.ProviderKey)
                .Select(x => new { x.UserId, x.LoginProvider, x.ProviderKey, x.ProviderDisplayName }).ToArrayAsync(),
            Tokens = await context.UserTokens.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.LoginProvider).ThenBy(x => x.Name)
                .Select(x => new { x.UserId, x.LoginProvider, x.Name, x.Value }).ToArrayAsync()
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot))));
    }

    /// <summary>Leest alleen het gemigreerde schema van deze geopende geheugenfixture.</summary>
    private static async Task<string[]> ReadStringsAsync(ApplicationDbContext context, string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values.ToArray();
    }
}
