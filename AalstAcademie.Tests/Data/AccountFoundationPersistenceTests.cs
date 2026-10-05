// Sprint 003: Behoudt de account- en profielasserties bij de vijf migraties
// De opleidingstabellen mogen bestaande Identity-opslag niet beschadigen.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AalstAcademie.Tests.Data;

/// <summary>Test het echte SQLite-schema, upgrades en constraints via EF-migraties.</summary>
public class AccountFoundationPersistenceTests
{
    // Sla beide profieltypen en beoordelingsgegevens op en lees ze opnieuw inclusief relaties.
    [Fact]
    public async Task Fresh_migrations_roundtrip_custom_fields_and_both_profile_types()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var reviewer = await db.AddUserAsync(RequestedAccountType.Employee, AccountApprovalStatus.Approved);
        var user = await db.AddUserAsync(RequestedAccountType.InternalInstructor, AccountApprovalStatus.Approved);
        var external = await db.AddUserAsync(RequestedAccountType.ExternalInstructor);
        user.ReviewedById = reviewer.Id;
        user.ReviewedAt = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
        user.IsBlocked = true;
        db.Context.InternalInstructors.Add(new InternalInstructor { ApplicationUserId = user.Id });
        db.Context.ExternalInstructors.Add(new ExternalInstructor
            { ApplicationUserId = external.Id, OrganizationName = "Demo Academie", VatNumber = "BE0123456789" });
        await db.Context.SaveChangesAsync();
        // Herladen bewijst roundtrip via SQLite, niet alleen het bijhouden van objecten door EF.
        db.Context.ChangeTracker.Clear();
        var stored = await db.Context.InternalInstructors.Include(x => x.User).ThenInclude(x => x.Department).SingleAsync();
        Assert.Equal(user.Id, stored.User.Id);
        Assert.Equal("Noor", stored.User.FirstName);
        Assert.Equal("Peeters", stored.User.LastName);
        Assert.Equal(user.DepartmentId, stored.User.DepartmentId);
        Assert.NotNull(stored.User.Department);
        Assert.Equal(user.Email, stored.User.Email);
        Assert.Equal(user.PhoneNumber, stored.User.PhoneNumber);
        Assert.Equal(reviewer.Id, stored.User.ReviewedById);
        Assert.Equal(user.ReviewedAt, stored.User.ReviewedAt);
        Assert.True(stored.User.IsBlocked);
        Assert.Equal(AccountApprovalStatus.Approved, stored.User.AccountApprovalStatus);
        Assert.Equal(RequestedAccountType.InternalInstructor, stored.User.RequestedAccountType);
        var storedExternal = await db.Context.ExternalInstructors.Include(x => x.User).SingleAsync();
        Assert.Equal("Demo Academie", storedExternal.OrganizationName);
        Assert.Equal("BE0123456789", storedExternal.VatNumber);
        Assert.Null(storedExternal.User.DepartmentId);
        Assert.Null(storedExternal.User.FirstName);
        // Sprint 003 revisie 02 splitst lege legacyopleidingen in definities en momenten zonder accountverlies.
        // De oorspronkelijke profiel-/Identity-roundtrip blijft dezelfde regressie bewaken.
        Assert.Equal(6, (await db.Context.Database.GetAppliedMigrationsAsync()).Count());
    }

    // De bestaande migratie vormt het vertrekpunt; pas daarna volgt AddAccountFoundation.
    [Fact]
    public async Task Upgrade_preserves_legacy_user_and_credentials_with_pending_nullable_defaults()
    {
        await using var db = await SqliteTestDatabase.CreateAsync(migrate: false);
        await db.Context.GetService<IMigrator>().MigrateAsync("00000000000000_CreateIdentitySchema");
        // Fictieve hash/stamps: geen toegang tot de werkelijke database of wachtwoorden.
        // Raw SQL kan de oude tabel vullen vóór de nieuwe ApplicationUser-kolommen bestaan.
        await db.Context.Database.ExecuteSqlRawAsync("""
            INSERT INTO AspNetUsers (Id, UserName, NormalizedUserName, Email, NormalizedEmail,
                PasswordHash, SecurityStamp, ConcurrencyStamp, EmailConfirmed, PhoneNumberConfirmed,
                TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
            VALUES ('legacy-demo', 'legacy@example.invalid', 'LEGACY@EXAMPLE.INVALID',
                'legacy@example.invalid', 'LEGACY@EXAMPLE.INVALID', 'fictitious-hash', 'fictitious-stamp',
                'fictitious-concurrency', 1, 0, 0, 1, 0)
            """);
        // Upgrade alleen deze geïsoleerde database en controleer zowel behoud als nieuwe defaults.
        await db.Context.Database.MigrateAsync();
        var user = await db.Context.Users.SingleAsync();
        Assert.Equal("legacy-demo", user.Id);
        Assert.Equal("legacy@example.invalid", user.Email);
        Assert.Equal("LEGACY@EXAMPLE.INVALID", user.NormalizedUserName);
        Assert.Equal("fictitious-hash", user.PasswordHash);
        Assert.Equal("fictitious-stamp", user.SecurityStamp);
        Assert.True(user.EmailConfirmed);
        Assert.Equal(AccountApprovalStatus.Pending, user.AccountApprovalStatus);
        Assert.False(user.IsBlocked);
        Assert.Null(user.RequestedAccountType);
        Assert.Null(user.FirstName);
        Assert.Null(user.LastName);
        Assert.Null(user.DepartmentId);
        Assert.Null(user.ManagerUserId);
        Assert.Null(user.ReviewedById);
        Assert.Null(user.ReviewedAt);
        Assert.Empty(await db.Context.UserRoles.ToListAsync());
    }

    // Elke profieltabel heeft een eigen unieke index op ApplicationUserId.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_profile_user_link_is_rejected(bool external)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var user = await db.AddUserAsync(external ? RequestedAccountType.ExternalInstructor : RequestedAccountType.InternalInstructor);
        void AddProfile()
        {
            if (external) db.Context.ExternalInstructors.Add(new ExternalInstructor { ApplicationUserId = user.Id, OrganizationName = "Demo" });
            else db.Context.InternalInstructors.Add(new InternalInstructor { ApplicationUserId = user.Id });
        }
        AddProfile();
        await db.Context.SaveChangesAsync();
        // Wis tracking: anders probeert EF bij de één-op-éénrelatie de vorige koppeling
        // te vervangen en bereikt de test niet de bedoelde unieke-indexovertreding.
        db.Context.ChangeTracker.Clear();
        AddProfile();
        await AssertConstraintAsync(() => db.Context.SaveChangesAsync(), 2067);
    }

    // Niet-bestaande sleutels moeten door SQLite worden afgewezen, ook zonder servicevalidatie.
    [Theory]
    [InlineData("internal")]
    [InlineData("external")]
    [InlineData("department")]
    [InlineData("reviewer")]
    public async Task Nonexistent_foreign_keys_are_rejected(string relationship)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        switch (relationship)
        {
            case "internal": db.Context.InternalInstructors.Add(new InternalInstructor { ApplicationUserId = "missing" }); break;
            case "external": db.Context.ExternalInstructors.Add(new ExternalInstructor { ApplicationUserId = "missing", OrganizationName = "Demo" }); break;
            case "department": db.Context.Users.Add(new ApplicationUser { DepartmentId = 999 }); break;
            case "reviewer": db.Context.Users.Add(new ApplicationUser { ReviewedById = "missing" }); break;
        }
        await AssertConstraintAsync(() => db.Context.SaveChangesAsync(), 787);
    }

    // Rechtstreeks DELETE test Restrict op databaseniveau, los van EF-navigatiebeheer.
    [Theory]
    [InlineData("department")]
    [InlineData("internal")]
    [InlineData("external")]
    [InlineData("reviewer")]
    public async Task Referenced_entities_cannot_be_deleted(string relationship)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var user = await db.AddUserAsync(RequestedAccountType.InternalInstructor);
        if (relationship == "internal") db.Context.InternalInstructors.Add(new InternalInstructor { ApplicationUserId = user.Id });
        if (relationship == "external") db.Context.ExternalInstructors.Add(new ExternalInstructor { ApplicationUserId = user.Id, OrganizationName = "Demo" });
        if (relationship == "reviewer") db.Context.Users.Add(new ApplicationUser { ReviewedById = user.Id });
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();
        // De afdelingssleutel komt uitsluitend uit eigen testdata; userId wordt als SQL-parameter gegeven.
        var sql = relationship == "department"
            ? $"DELETE FROM Departments WHERE Id = {user.DepartmentId}"
            : "DELETE FROM AspNetUsers WHERE Id = @id";
        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            db.Context.Database.ExecuteSqlRawAsync(sql, new SqliteParameter("@id", user.Id)));
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal(1, await db.Context.Users.CountAsync(x => x.Id == user.Id));
    }

    // De CHECK-constraints moeten ook tekst met uitsluitend spaties afwijzen.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blank_required_reference_names_are_rejected(bool external)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        if (external)
        {
            var user = await db.AddUserAsync(RequestedAccountType.ExternalInstructor);
            db.Context.ExternalInstructors.Add(new ExternalInstructor { ApplicationUserId = user.Id, OrganizationName = "  " });
        }
        else db.Context.Departments.Add(new Department { Name = "  " });
        await AssertConstraintAsync(() => db.Context.SaveChangesAsync(), 275);
    }

    // Twee gelijknamige afdelingen mogen niet onafhankelijk naast elkaar ontstaan.
    [Fact]
    public async Task Department_names_are_unique()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        db.Context.Departments.Add(new Department { Name = "ICT" });
        await db.Context.SaveChangesAsync();
        db.Context.Departments.Add(new Department { Name = "ICT" });
        await AssertConstraintAsync(() => db.Context.SaveChangesAsync(), 2067);
    }

    /// <summary>Controleert de echte SQLite-fout, niet uitsluitend een algemene EF-exception.</summary>
    private static async Task AssertConstraintAsync(Func<Task<int>> action, int extendedCode)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(action);
        var sqlite = Assert.IsType<SqliteException>(exception.InnerException);
        // 19 = constraintfout; extendedCode onderscheidt UNIQUE, FOREIGN KEY en CHECK.
        Assert.Equal(19, sqlite.SqliteErrorCode);
        Assert.Equal(extendedCode, sqlite.SqliteExtendedErrorCode);
    }
}
