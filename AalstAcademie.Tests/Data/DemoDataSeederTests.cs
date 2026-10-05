// Sprint 003: Controleert de zes fictieve accounts, rollen en profielen in de nieuwe bootstrap
// Een effectieve nieuwe demostart bouwt haar eigen database opnieuw op.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AalstAcademie.Tests.Data;

/// <summary>Bewijst de demogrens, herkenning en volledige bootstrap met echte SQLite/Identity-opslag.</summary>
public class DemoDataSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const string AdministratorId = "demo-001-administrator";
    private const string AdministratorKey = "sprint002.administrator";
    private const string AdministratorEmail = "beheerder@example.invalid";

    // Een vlag alleen is geen toestemming: buiten Development moet de configuratie falen.
    [Fact]
    public void Effective_demo_requires_development_and_an_explicit_enabled_flag()
    {
        Assert.True(DemoMode.From(new TestEnvironment(Environments.Development), Settings(true)).IsEnabled);
        Assert.False(DemoMode.From(new TestEnvironment(Environments.Development), Settings(false)).IsEnabled);
        Assert.False(DemoMode.From(new TestEnvironment(Environments.Production), Settings(false)).IsEnabled);
        Assert.False(DemoMode.From(new TestEnvironment(Environments.Development), new ConfigurationBuilder().Build()).IsEnabled);
        Assert.Throws<InvalidOperationException>(() => DemoMode.From(new TestEnvironment(Environments.Production), Settings(true)));
    }

    // Ook een rechtstreekse seeder-aanroep mag bij uitgeschakelde demomode niets aanmaken.
    [Fact]
    public async Task Disabled_demo_returns_no_credentials_and_does_not_create_accounts()
    {
        await using var db = await PreparedDatabaseAsync();
        var result = await Seeder(db, enabled: false).SeedAsync();
        Assert.False(result.IsSeeded);
        Assert.Equal(0, result.CreatedAccountCount);
        Assert.Equal(0, await db.Context.Users.CountAsync());
        Assert.Equal(0, await db.Context.InternalInstructors.CountAsync());
        Assert.Equal(0, await db.Context.ExternalInstructors.CountAsync());
    }

    // Controleer het opgeslagen resultaat en echte wachtwoordverificatie zonder credentials af te drukken.
    [Fact]
    public async Task First_seed_creates_six_representative_accounts_profiles_roles_and_transient_credentials()
    {
        await using var db = await PreparedDatabaseAsync();
        var manager = db.Services.GetRequiredService<UserManager<ApplicationUser>>();
        var credentials = new DemoCredentials();
        var result = await Seeder(db, credentials: credentials).SeedAsync();
        Assert.True(result.IsSeeded);
        Assert.False(credentials.IsReady);
        // De unitcaller heeft foundation plus hele batch voltooid; alleen dan volgt de normale publicatie.
        credentials.PublishReady(result);
        Assert.True(credentials.TryGetReadyHint(out var hint));
        db.Context.ChangeTracker.Clear();
        var users = await db.Context.Users.OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(6, users.Length);
        Assert.Equal(6, result.CreatedAccountCount);
        Assert.Equal(6, hint!.Emails.Count);
        Assert.Equal(1, await db.Context.InternalInstructors.CountAsync());
        Assert.Equal(2, await db.Context.ExternalInstructors.CountAsync());
        var administrator = Assert.Single(users, x => x.Id == AdministratorId);
        Assert.Equal(AdministratorKey, administrator.DemoSeedKey);
        Assert.Null(administrator.RequestedAccountType);
        Assert.Equal(AccountApprovalStatus.Approved, administrator.AccountApprovalStatus);
        Assert.Equal(new[] { RoleNames.Beheerder }, await manager.GetRolesAsync(administrator));
        foreach (var user in users)
        {
            Assert.False(user.EmailConfirmed);
            Assert.False(user.IsBlocked);
            Assert.EndsWith("@example.invalid", user.Email);
            Assert.Equal(manager.NormalizeEmail(user.Email!), user.NormalizedEmail);
            Assert.Contains(user.Email!, hint.Emails);
            Assert.True(hint.Password.Length == 12);
            Assert.True(await manager.CheckPasswordAsync(user, hint.Password));
            Assert.False(hint.ToString().Contains(hint.Password, StringComparison.Ordinal));
            Assert.True(!string.IsNullOrEmpty(user.SecurityStamp) && !string.IsNullOrEmpty(user.ConcurrencyStamp));
            if (user.Id == AdministratorId) continue;
            Assert.Equal(Now.UtcDateTime, user.AccountRequestedAtUtc);
            var expectedRoles = user.AccountApprovalStatus == AccountApprovalStatus.Approved
                ? RoleNames.ForApprovedAccountType(user.RequestedAccountType).Order().ToArray()
                : [];
            Assert.Equal(expectedRoles, (await manager.GetRolesAsync(user)).Order().ToArray());
            if (user.AccountApprovalStatus == AccountApprovalStatus.Approved)
            {
                Assert.Equal(AdministratorId, user.ReviewedById);
                Assert.Equal(Now, user.ReviewedAt);
            }
            else
            {
                Assert.Null(user.ReviewedById);
                Assert.Null(user.ReviewedAt);
            }
            var external = user.RequestedAccountType == RequestedAccountType.ExternalInstructor;
            Assert.Equal(external, await db.Context.ExternalInstructors.AnyAsync(x => x.ApplicationUserId == user.Id));
            Assert.Equal(user.RequestedAccountType == RequestedAccountType.InternalInstructor,
                await db.Context.InternalInstructors.AnyAsync(x => x.ApplicationUserId == user.Id));
            if (external) Assert.Null(user.DepartmentId);
            else Assert.NotNull(user.DepartmentId);
        }
        Assert.Equal(1, users.Count(x => x.RequestedAccountType == RequestedAccountType.Employee && x.AccountApprovalStatus == AccountApprovalStatus.Approved));
        Assert.Equal(1, users.Count(x => x.RequestedAccountType == RequestedAccountType.InternalInstructor));
        Assert.Equal(DemoAccountCatalog.InternalInstructorId, Assert.Single(users, x => x.RequestedAccountType == RequestedAccountType.InternalInstructor).ManagerUserId);
        Assert.Equal(1, users.Count(x => x.RequestedAccountType == RequestedAccountType.ExternalInstructor && x.AccountApprovalStatus == AccountApprovalStatus.Approved));
        Assert.Equal(1, users.Count(x => x.RequestedAccountType == RequestedAccountType.Employee && x.AccountApprovalStatus == AccountApprovalStatus.Pending));
        Assert.Equal(1, users.Count(x => x.RequestedAccountType == RequestedAccountType.ExternalInstructor && x.AccountApprovalStatus == AccountApprovalStatus.Pending));
    }

    // Een tweede losse seeder-aanroep in dezelfde sessie is een fout en mag geen bestaande account repareren.
    [Fact]
    public async Task Second_seed_is_refused_and_preserves_existing_account_changes_without_repair()
    {
        await using var db = await PreparedDatabaseAsync();
        var credentials = new DemoCredentials();
        var service = Seeder(db, credentials: credentials);
        credentials.PublishReady(await service.SeedAsync());
        Assert.True(credentials.TryGetReadyHint(out var hint));
        var manager = db.Services.GetRequiredService<UserManager<ApplicationUser>>();
        var administrator = (await manager.FindByIdAsync(AdministratorId))!;
        var originalPassword = hint!.Password;
        var changedPassword = AccountWebApplicationFactory.NewPassword();
        Assert.True((await manager.ChangePasswordAsync(administrator, originalPassword, changedPassword)).Succeeded);
        Assert.True((await manager.RemoveFromRoleAsync(administrator, RoleNames.Beheerder)).Succeeded);
        administrator.IsBlocked = true;
        administrator.AccountApprovalStatus = AccountApprovalStatus.Refused;
        administrator.RefusalReason = "Bewust ingetrokken voor de test.";
        Assert.True((await manager.UpdateAsync(administrator)).Succeeded);
        var before = await StoredAccountFingerprintAsync(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SeedAsync());
        Assert.Equal(before, await StoredAccountFingerprintAsync(db));
        Assert.Equal(0, await db.Context.UserRoles.CountAsync(x => x.UserId == AdministratorId));
        Assert.True(await manager.CheckPasswordAsync((await manager.FindByIdAsync(AdministratorId))!, changedPassword));
    }

    // Vier onafhankelijk gevaarlijke botsingen mogen nooit een bestaand account adopteren.
    [Theory]
    [InlineData("id")]
    [InlineData("email")]
    [InlineData("marker")]
    [InlineData("incomplete-triple")]
    public async Task Reserved_identity_email_marker_or_mismatched_triple_aborts_without_partial_seed(string collision)
    {
        await using var db = await PreparedDatabaseAsync();
        var email = collision is "email" or "incomplete-triple" ? AdministratorEmail.ToUpperInvariant() : "bestaand@example.invalid";
        var user = new ApplicationUser
        {
            Id = collision is "id" or "incomplete-triple" ? AdministratorId : "existing-unreserved-account",
            UserName = email, Email = email,
            DemoSeedKey = collision == "marker" ? AdministratorKey : null
        };
        var manager = db.Services.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True((await manager.CreateAsync(user)).Succeeded);
        var before = await StoredAccountFingerprintAsync(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Seeder(db).SeedAsync());
        Assert.Equal(before, await StoredAccountFingerprintAsync(db));
        Assert.Equal(1, await db.Context.Users.CountAsync());
        Assert.Equal(0, await db.Context.UserRoles.CountAsync());
        Assert.Equal(0, await db.Context.InternalInstructors.CountAsync());
        Assert.Equal(0, await db.Context.ExternalInstructors.CountAsync());
    }

    // De fout ontstaat nadat meerdere accounts en een profiel zijn opgeslagen in de nog open transactie.
    [Fact]
    public async Task Late_role_failure_rolls_back_the_entire_batch_and_a_clean_retry_succeeds()
    {
        await using var db = await PreparedDatabaseAsync();
        await db.Context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectDemoInstructorRole BEFORE INSERT ON AspNetUserRoles
            WHEN NEW.UserId = 'demo-003-internal-instructor'
            BEGIN SELECT RAISE(ABORT, 'Injected demo role failure'); END;
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Seeder(db).SeedAsync());
        Assert.Equal(0, await db.Context.Users.CountAsync());
        Assert.Equal(0, await db.Context.UserRoles.CountAsync());
        Assert.Equal(0, await db.Context.InternalInstructors.CountAsync());
        Assert.Equal(0, await db.Context.ExternalInstructors.CountAsync());
        Assert.Equal(3, await db.Context.Roles.CountAsync());
        Assert.Equal(4, await db.Context.Departments.CountAsync());
        await db.Context.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectDemoInstructorRole;");
        Assert.Equal(6, (await Seeder(db).SeedAsync()).CreatedAccountCount);
    }

    /// <summary>Bereidt uitsluitend foundation-referentiedata voor; de demoseeder blijft de te testen operatie.</summary>
    private static async Task<SqliteTestDatabase> PreparedDatabaseAsync()
    {
        var db = await SqliteTestDatabase.CreateAsync();
        await db.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        return db;
    }

    /// <summary>Gebruikt echte managers en profielservice met een vaste klok, zodat seedmetadata voorspelbaar blijft.</summary>
    private static DemoDataSeeder Seeder(SqliteTestDatabase db, bool enabled = true, DemoCredentials? credentials = null) =>
        new(db.Context, db.Services.GetRequiredService<UserManager<ApplicationUser>>(),
            db.Services.GetRequiredService<InstructorProfileService>(), new DemoMode(enabled), new FrozenTimeProvider(), credentials ?? new DemoCredentials());

    /// <summary>De vlag komt uit configuratie; de effectieve demogrens onderzoekt daarnaast de hostomgeving.</summary>
    private static IConfiguration Settings(bool enabled) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Enabled"] = enabled.ToString() }).Build();

    // Bewaar hashes en stamps als vergelijking; er worden nooit wachtwoorden in het snapshot opgeslagen.
    private static async Task<string[]> StoredAccountFingerprintAsync(SqliteTestDatabase db) =>
        await db.Context.Users.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => x.Id + "|" + x.DemoSeedKey + "|" + x.AccountApprovalStatus + "|" + x.IsBlocked + "|" +
                x.RefusalReason + "|" + x.PasswordHash + "|" + x.SecurityStamp + "|" + x.ConcurrencyStamp).ToArrayAsync();

    /// <summary>Vermijdt tijdsafhankelijke assertions zonder de systeemklok van andere apps te wijzigen.</summary>
    private sealed class FrozenTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Test Development/Production-keuzes zonder een echte server of globale environmentvariabele.</summary>
    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "AalstAcademie.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
