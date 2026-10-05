using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Accounts;

/// <summary>
/// Eenentwintig SQLite-integratiegevallen bewaken profielaanmaak, veilige weigering,
/// idempotentie, ongewijzigde rechten en herstel na een opslagconflict.
/// </summary>
public class InstructorProfileServiceTests
{
    // Test intern/extern voor zowel Pending als Approved: geen van beide mag extra rechten krijgen.
    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    public async Task Matching_profile_is_created_once_without_approval_or_privileges(int type, int approval)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var user = await db.AddUserAsync((RequestedAccountType)type, (AccountApprovalStatus)approval);
        var service = db.Services.GetRequiredService<InstructorProfileService>();
        string? organization = type == 3 ? "Demo Academie" : null;
        // Act: eerste aanmaak en herhaling. Nieuwe externe input mag de oude naam niet vervangen.
        Assert.Equal(InstructorProfileOutcome.Created, await service.CreateAsync(user.Id, organization));
        Assert.Equal(InstructorProfileOutcome.AlreadyExists, await service.CreateAsync(user.Id, type == 3 ? "Andere naam" : null));
        Assert.Equal(type == 2 ? 1 : 0, await db.Context.InternalInstructors.CountAsync());
        Assert.Equal(type == 3 ? 1 : 0, await db.Context.ExternalInstructors.CountAsync());
        if (type == 3) Assert.Equal("Demo Academie", (await db.Context.ExternalInstructors.SingleAsync()).OrganizationName);
        // Controleer het opgeslagen account opnieuw om stilzwijgende goedkeuring/mutatie uit te sluiten.
        db.Context.ChangeTracker.Clear();
        var stored = await db.Context.Users.SingleAsync();
        Assert.Equal((AccountApprovalStatus)approval, stored.AccountApprovalStatus);
        Assert.Equal((RequestedAccountType)type, stored.RequestedAccountType);
        Assert.Equal(user.DepartmentId, stored.DepartmentId);
        Assert.Equal(user.FirstName, stored.FirstName);
        Assert.Null(stored.ReviewedAt);
        Assert.Empty(await db.Context.UserRoles.ToListAsync());
    }

    // Medewerker, onvolledig type en onbekende enum zijn geen lesgeveraanvragen.
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(99)]
    public async Task Unsupported_account_type_cannot_provision_a_profile(int type)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var user = await db.AddUserAsync(type == 0 ? null : (RequestedAccountType)type);
        Assert.Equal(InstructorProfileOutcome.WrongAccountType,
            await db.Services.GetRequiredService<InstructorProfileService>().CreateAsync(user.Id, "Demo"));
        await AssertNoProfilesOrGrantsAsync(db);
    }

    // Een onbekend userId levert een typed resultaat op, zonder gedeeltelijk profiel.
    [Fact]
    public async Task Missing_user_returns_a_controlled_outcome()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        Assert.Equal(InstructorProfileOutcome.UserNotFound,
            await db.Services.GetRequiredService<InstructorProfileService>().CreateAsync("missing"));
        await AssertNoProfilesOrGrantsAsync(db);
    }

    // Afwijzing en blokkering gelden ook als het profiel verder volledig lijkt.
    [Theory]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task Ineligible_user_is_rejected_without_writes(int approval, bool blocked)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var user = await db.AddUserAsync(RequestedAccountType.InternalInstructor, (AccountApprovalStatus)approval, blocked);
        Assert.Equal(InstructorProfileOutcome.IneligibleAccount,
            await db.Services.GetRequiredService<InstructorProfileService>().CreateAsync(user.Id));
        await AssertNoProfilesOrGrantsAsync(db);
    }

    // Bouw bewust strijdige opslag op om beide richtingen van de service-invariant te toetsen.
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Existing_opposite_profile_is_a_conflict_including_dual_profile_data(int type)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var user = await db.AddUserAsync((RequestedAccountType)type);
        if (type == 2) db.Context.ExternalInstructors.Add(new ExternalInstructor { ApplicationUserId = user.Id, OrganizationName = "Legacy conflict" });
        else db.Context.InternalInstructors.Add(new InternalInstructor { ApplicationUserId = user.Id });
        await db.Context.SaveChangesAsync();
        var service = db.Services.GetRequiredService<InstructorProfileService>();
        Assert.Equal(InstructorProfileOutcome.Conflict, await service.CreateAsync(user.Id, type == 3 ? "Demo" : null));
        Assert.Equal(1, await db.Context.InternalInstructors.CountAsync() + await db.Context.ExternalInstructors.CountAsync());
        // Rechtstreekse opslag omzeilt de service bewust: losse tabelindexen kunnen
        // twee verschillende profieltypen voor hetzelfde account niet verhinderen.
        if (type == 2) db.Context.InternalInstructors.Add(new InternalInstructor { ApplicationUserId = user.Id });
        else db.Context.ExternalInstructors.Add(new ExternalInstructor { ApplicationUserId = user.Id, OrganizationName = "Demo" });
        await db.Context.SaveChangesAsync();
        Assert.Equal(InstructorProfileOutcome.Conflict, await service.CreateAsync(user.Id, type == 3 ? "Demo" : null));
        Assert.Equal(2, await db.Context.InternalInstructors.CountAsync() + await db.Context.ExternalInstructors.CountAsync());
        Assert.Empty(await db.Context.UserRoles.ToListAsync());
    }

    // Ontbrekende interne naam of externe organisatienaam moet vóór de insert worden afgewezen.
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Invalid_complete_profile_leaves_no_partial_write_or_user_mutation(int type)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var user = await db.AddUserAsync((RequestedAccountType)type);
        if (type == 2) { user.LastName = null; await db.Context.SaveChangesAsync(); }
        Assert.Equal(InstructorProfileOutcome.InvalidProfile,
            await db.Services.GetRequiredService<InstructorProfileService>().CreateAsync(user.Id));
        await AssertNoProfilesOrGrantsAsync(db);
        db.Context.ChangeTracker.Clear();
        var stored = await db.Context.Users.SingleAsync();
        Assert.Null(stored.LastName);
        Assert.Equal(AccountApprovalStatus.Pending, stored.AccountApprovalStatus);
        Assert.Equal(user.DepartmentId, stored.DepartmentId);
        Assert.Equal(user.RequestedAccountType, stored.RequestedAccountType);
    }

    // Een caller kan een intern account niet via organisatieparameters extern maken.
    [Fact]
    public async Task Internal_account_rejects_external_inputs()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var user = await db.AddUserAsync(RequestedAccountType.InternalInstructor);
        Assert.Equal(InstructorProfileOutcome.InvalidProfile,
            await db.Services.GetRequiredService<InstructorProfileService>().CreateAsync(user.Id, "Demo", "BE0123456789"));
        await AssertNoProfilesOrGrantsAsync(db);
    }

    /// <summary>Gedeelde bewijslast voor foutpaden: geen profielen en geen gebruikersrollen.</summary>
    private static async Task AssertNoProfilesOrGrantsAsync(SqliteTestDatabase db)
    {
        Assert.Empty(await db.Context.InternalInstructors.ToListAsync());
        Assert.Empty(await db.Context.ExternalInstructors.ToListAsync());
        Assert.Empty(await db.Context.UserRoles.ToListAsync());
    }

    // Injecteer een echte dubbele insert op precies het conflictpad; dit is geen stress-/belastingtest.
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Unique_index_race_returns_conflict_rolls_back_and_allows_a_clean_retry(int type)
    {
        var competingWriter = new CompetingProfileInsert();
        await using var db = await SqliteTestDatabase.CreateAsync(interceptor: competingWriter);
        var user = await db.AddUserAsync((RequestedAccountType)type);
        var service = db.Services.GetRequiredService<InstructorProfileService>();
        // Pas na het opslaan van het testaccount mag de interceptor de profielinsert beïnvloeden.
        competingWriter.Armed = true;
        Assert.Equal(InstructorProfileOutcome.Conflict, await service.CreateAsync(user.Id, type == 3 ? "Demo" : null));
        Assert.True(competingWriter.Injected);
        await AssertNoProfilesOrGrantsAsync(db);
        // Rollback verwijdert de SQL-insert; Detach voorkomt dat EF hem later opnieuw probeert.
        Assert.DoesNotContain(db.Context.ChangeTracker.Entries(), x => x.State == EntityState.Added);
        Assert.Equal(InstructorProfileOutcome.Created, await service.CreateAsync(user.Id, type == 3 ? "Demo" : null));
    }

    // Sprint 002 laat registratie en demoseed de volledige user-plus-profieloperatie beheren.
    // De helper start geen geneste transactie en laat de eigenaar de volledige operatie committen.
    [Fact]
    public async Task Existing_transaction_is_joined_and_outer_owner_can_commit()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var user = await db.AddUserAsync(RequestedAccountType.InternalInstructor);
        await using var outer = await db.Context.Database.BeginTransactionAsync();
        Assert.Equal(InstructorProfileOutcome.Created,
            await db.Services.GetRequiredService<InstructorProfileService>().CreateAsync(user.Id));
        Assert.Same(outer, db.Context.Database.CurrentTransaction);
        await outer.CommitAsync();
        Assert.Equal(user.Id, (await db.Context.InternalInstructors.SingleAsync()).ApplicationUserId);
    }

    // Deze uitbreiding toetst dat de helper geen voortijdige commit uitvoert voor zijn caller.
    // Account en profiel mogen tijdens de transactie zichtbaar zijn, maar verdwijnen samen bij rollback.
    [Fact]
    public async Task Outer_rollback_removes_both_new_account_and_profile_without_helper_commit()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await using (var outer = await db.Context.Database.BeginTransactionAsync())
        {
            var user = await db.AddUserAsync(RequestedAccountType.ExternalInstructor);
            Assert.Equal(InstructorProfileOutcome.Created,
                await db.Services.GetRequiredService<InstructorProfileService>().CreateAsync(user.Id, "Fictieve Academie"));
            Assert.Same(outer, db.Context.Database.CurrentTransaction);
            await outer.RollbackAsync();
        }
        db.Context.ChangeTracker.Clear();
        Assert.Empty(await db.Context.Users.ToListAsync());
        await AssertNoProfilesOrGrantsAsync(db);
    }

    // De caller behoudt eigenaarschap over tracking en rollback, ook als profielaanmaak een conflict oplevert.
    // Bij een conflict blijft de buitenste transactie bruikbaar; alleen de helperinsert wordt losgekoppeld.
    [Fact]
    public async Task Joined_profile_failure_keeps_outer_transaction_and_unrelated_tracking_owned_by_caller()
    {
        var competingWriter = new CompetingProfileInsert();
        await using var db = await SqliteTestDatabase.CreateAsync(interceptor: competingWriter);
        var user = await db.AddUserAsync(RequestedAccountType.InternalInstructor);
        await using var outer = await db.Context.Database.BeginTransactionAsync();
        var unrelated = new Department { Name = "Andere operatie" };
        db.Context.Departments.Add(unrelated);
        competingWriter.Armed = true;
        Assert.Equal(InstructorProfileOutcome.Conflict,
            await db.Services.GetRequiredService<InstructorProfileService>().CreateAsync(user.Id));
        Assert.True(competingWriter.Injected);
        Assert.Same(outer, db.Context.Database.CurrentTransaction);
        Assert.Equal(EntityState.Added, db.Context.Entry(unrelated).State);
        Assert.DoesNotContain(db.Context.ChangeTracker.Entries<InternalInstructor>(), x => x.State == EntityState.Added);
        await outer.RollbackAsync();
    }

    // Voer één directe insert uit tussen servicecontrole en service-insert. Daardoor ontstaat
    // een echte SQLite-UNIQUE-fout zonder onvoorspelbare timing tussen meerdere threads.
    private sealed class CompetingProfileInsert : SaveChangesInterceptor
    {
        // Armed schakelt eenmalige injectie in; Injected bewijst dat dat pad werkelijk liep.
        public bool Armed { get; set; }
        public bool Injected { get; private set; }

        /// <summary>Onderschept de opslag vlak vóór EF de toegevoegde profielentiteit schrijft.</summary>
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            // Uitschakelen maakt een schone herhaling na het conflict mogelijk.
            Armed = false;
            var context = eventData.Context!;
            var internalProfile = context.ChangeTracker.Entries<InternalInstructor>()
                .SingleOrDefault(x => x.State == EntityState.Added)?.Entity;
            // ExecuteSqlInterpolatedAsync parameteriseert de waarden; de test schrijft
            // binnen dezelfde service-transactie zodat rollback ook deze insert terugdraait.
            if (internalProfile is not null)
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO InternalInstructors (ApplicationUserId) VALUES ({internalProfile.ApplicationUserId})", cancellationToken);
            else
            {
                var profile = context.ChangeTracker.Entries<ExternalInstructor>().Single(x => x.State == EntityState.Added).Entity;
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO ExternalInstructors (ApplicationUserId, OrganizationName) VALUES ({profile.ApplicationUserId}, {"Concurrent demo"})", cancellationToken);
            }
            Injected = true;
            return result;
        }
    }
}
