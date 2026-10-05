// Sprint 003: Controleert behoud van de opgeslagen accountbeoordeling bij een upgrade
// De aangepaste migratietelling verandert de reviewregels niet.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AalstAcademie.Tests.Data;

/// <summary>Bewijst dat de nieuwe migratie toevoegt zonder bestaande accountgegevens te vervangen.</summary>
public class AccountReviewPersistenceTests
{
    // Eerst het sprint-001-schema, dan de nieuwe migratie: zo toetsen we upgradebehoud in plaats van alleen nieuwbouw.
    [Fact]
    public async Task Upgrade_preserves_foundation_user_profile_roles_and_credentials()
    {
        await using var db = await SqliteTestDatabase.CreateAsync(migrate: false);
        await db.Context.GetService<IMigrator>().MigrateAsync("20261002124853_AddAccountFoundation");
        // SQL vult het oude schema; het huidige EF-model bevat de nieuwe kolommen al.
        await db.Context.Database.ExecuteSqlRawAsync("""
            INSERT INTO Departments (Id,Name) VALUES (91,'Testafdeling');
            INSERT INTO AspNetUsers (Id,UserName,NormalizedUserName,Email,NormalizedEmail,
                PasswordHash,SecurityStamp,ConcurrencyStamp,EmailConfirmed,PhoneNumberConfirmed,
                TwoFactorEnabled,LockoutEnabled,AccessFailedCount,RequestedAccountType,
                AccountApprovalStatus,IsBlocked,FirstName,LastName,DepartmentId)
            VALUES ('foundation-user','foundation@example.invalid','FOUNDATION@EXAMPLE.INVALID',
                'foundation@example.invalid','FOUNDATION@EXAMPLE.INVALID','test-hash','test-security',
                'test-version',1,0,0,1,0,2,1,0,'Noor','Test',91);
            INSERT INTO InternalInstructors (ApplicationUserId) VALUES ('foundation-user');
            INSERT INTO AspNetRoles (Id,Name,NormalizedName) VALUES ('test-role','Lesgever','LESGEVER');
            INSERT INTO AspNetUserRoles (UserId,RoleId) VALUES ('foundation-user','test-role');
            """);
        await db.Context.Database.MigrateAsync();
        var user = await db.Context.Users.SingleAsync();
        Assert.Equal("test-hash", user.PasswordHash);
        Assert.Equal("test-security", user.SecurityStamp);
        Assert.Equal("test-version", user.ConcurrencyStamp);
        Assert.True(user.EmailConfirmed);
        Assert.Equal(AccountApprovalStatus.Approved, user.AccountApprovalStatus);
        Assert.Equal(91, user.DepartmentId);
        Assert.Equal("Noor", user.FirstName);
        Assert.Null(user.AccountRequestedAtUtc);
        Assert.Null(user.RefusalReason);
        Assert.Null(user.DemoSeedKey);
        Assert.Equal(user.Id, (await db.Context.InternalInstructors.SingleAsync()).ApplicationUserId);
        Assert.Equal(user.Id, (await db.Context.UserRoles.SingleAsync()).UserId);
        // De vijfde migratie laat deze account-/profiel-/credentialasserties intact.
        Assert.Equal(6, (await db.Context.Database.GetAppliedMigrationsAsync()).Count());
    }

    // NULL markeert gewone accounts; alleen een concrete DemoSeedKey moet uniek zijn voor herstart-herkenning.
    [Fact]
    public async Task Seed_marker_allows_multiple_nulls_but_rejects_duplicate_concrete_key()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var first = await db.AddUserAsync(RequestedAccountType.Employee);
        var second = await db.AddUserAsync(RequestedAccountType.Employee);
        Assert.Equal(2, await db.Context.Users.CountAsync(x => x.DemoSeedKey == null));
        first.DemoSeedKey = "test.marker";
        await db.Context.SaveChangesAsync();
        second.DemoSeedKey = first.DemoSeedKey;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
    }

    // Aanvraagdatum en weigeringmetadata zijn zelfstandige businessgegevens; zij bevestigen geen e-mailadres.
    [Fact]
    public async Task Request_and_refusal_metadata_roundtrip_without_email_confirmation()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var reviewer = await db.AddUserAsync(null, AccountApprovalStatus.Approved);
        var user = await db.AddUserAsync(RequestedAccountType.Employee, AccountApprovalStatus.Refused);
        var requested = new DateTime(2026, 10, 2, 9, 15, 0, DateTimeKind.Utc);
        user.AccountRequestedAtUtc = requested;
        user.RefusalReason = "Afdeling kon niet worden bevestigd.";
        user.ReviewedById = reviewer.Id;
        user.ReviewedAt = new DateTimeOffset(requested.AddHours(1));
        user.DemoSeedKey = "test.roundtrip";
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();
        var stored = await db.Context.Users.SingleAsync(x => x.Id == user.Id);
        Assert.Equal(requested, stored.AccountRequestedAtUtc);
        Assert.Equal(user.RefusalReason, stored.RefusalReason);
        Assert.Equal(reviewer.Id, stored.ReviewedById);
        Assert.Equal(user.ReviewedAt, stored.ReviewedAt);
        Assert.Equal("test.roundtrip", stored.DemoSeedKey);
        Assert.False(stored.EmailConfirmed);
    }
}
