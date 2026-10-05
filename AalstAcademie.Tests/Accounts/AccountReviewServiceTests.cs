using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AalstAcademie.Tests.Accounts;

/// <summary>Vijfentwintig scenario's toetsen beoordeling, actuele rechten en echte SQLite-atomiciteit.</summary>
public class AccountReviewServiceTests
{
    // De drie rollencombinaties zijn onafhankelijk van EmailConfirmed en blijven exact begrensd.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Approval_maps_each_type_to_exact_roles_and_rotates_session_version(int type)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, (RequestedAccountType)type,
            roles: [RoleNames.Medewerker, RoleNames.Lesgever]);
        var oldStamp = target.SecurityStamp;
        var oldVersion = target.ConcurrencyStamp;
        var result = await ReviewAsync(db.Services, actor, target, AccountReviewAction.Approve,
            type == 2 ? target.DepartmentId : null);
        Assert.Equal(AccountOperationStatus.Success, result.Status);
        Assert.Equal(target.Id, result.UserId);
        var stored = await ReloadAsync(db.Context, target.Id);
        Assert.Equal(AccountApprovalStatus.Approved, stored.AccountApprovalStatus);
        Assert.Equal((RequestedAccountType)type, stored.RequestedAccountType);
        Assert.Equal(actor.Id, stored.ReviewedById);
        Assert.Equal(db.Services.GetRequiredService<FixedTimeProvider>().UtcNow, stored.ReviewedAt);
        Assert.NotEqual(oldStamp, stored.SecurityStamp);
        Assert.NotEqual(oldVersion, stored.ConcurrencyStamp);
        Assert.False(stored.EmailConfirmed);
        Assert.Equal(RoleNames.ForApprovedAccountType((RequestedAccountType)type).Order(),
            (await db.Services.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(stored)).Order());
        Assert.Equal(1, Notifier(db.Services).Count);
    }

    // Een verkeerde afdelingssleutel schrijft niets; een bestaande correctie wijzigt dezelfde centrale FK.
    [Fact]
    public async Task Internal_department_is_validated_and_corrected_before_approval()
    {
        await using var db = await FileSqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.InternalInstructor);
        var originalDepartment = target.DepartmentId;
        var rejected = await ReviewAsync(db.Services, actor, target, AccountReviewAction.Approve, int.MaxValue);
        Assert.Equal(AccountOperationStatus.ValidationFailure, rejected.Status);
        Assert.Equal(originalDepartment, (await ReloadAsync(db.Context, target.Id)).DepartmentId);
        Assert.Equal(0, Notifier(db.Services).Count);
        var corrected = await db.Context.Departments.Where(x => x.Id != originalDepartment).Select(x => x.Id).FirstAsync();
        var notificationAfterCommit = false;
        Notifier(db.Services).OnNotifyAsync = async _ =>
        {
            // Een tweede verbinding moet de volledige beslissing al kunnen zien bij de melding.
            await using var read = db.CreateScope();
            var committed = await read.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking()
                .SingleAsync(x => x.Id == target.Id);
            notificationAfterCommit = db.Context.Database.CurrentTransaction is null &&
                committed.AccountApprovalStatus == AccountApprovalStatus.Approved && committed.DepartmentId == corrected;
        };
        Assert.True((await ReviewAsync(db.Services, actor, target, AccountReviewAction.Approve, corrected)).Succeeded);
        Assert.True(notificationAfterCommit);
        var profile = await db.Context.InternalInstructors.AsNoTracking().Include(x => x.User)
            .SingleAsync(x => x.ApplicationUserId == target.Id);
        Assert.Equal(corrected, profile.User.DepartmentId);
        Assert.Equal(RequestedAccountType.InternalInstructor, profile.User.RequestedAccountType);
        Assert.Empty(await db.Context.ExternalInstructors.Where(x => x.ApplicationUserId == target.Id).ToListAsync());
    }

    // Ook eventuele oude deelnemerrollen verdwijnen bij weigering; de oorspronkelijke aanvraagdatum blijft staan.
    [Fact]
    public async Task Refusal_records_trimmed_reason_reviewer_time_and_removes_participant_roles()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.Employee,
            roles: [RoleNames.Medewerker, RoleNames.Lesgever]);
        var requested = target.AccountRequestedAtUtc;
        var stamp = target.SecurityStamp;
        Assert.True((await ReviewAsync(db.Services, actor, target, AccountReviewAction.Refuse, reason: "  Onvolledige aanvraag.  ")).Succeeded);
        var stored = await ReloadAsync(db.Context, target.Id);
        Assert.Equal(AccountApprovalStatus.Refused, stored.AccountApprovalStatus);
        Assert.Equal("Onvolledige aanvraag.", stored.RefusalReason);
        Assert.Equal(actor.Id, stored.ReviewedById);
        Assert.Equal(db.Services.GetRequiredService<FixedTimeProvider>().UtcNow, stored.ReviewedAt);
        Assert.Equal(requested, stored.AccountRequestedAtUtc);
        Assert.NotEqual(stamp, stored.SecurityStamp);
        Assert.False(stored.EmailConfirmed);
        Assert.Empty(await db.Services.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(stored));
        Assert.Equal(1, Notifier(db.Services).Count);
    }

    // Heropenen verleent geen rechten en maakt de vroegere weigeringreden niet langer actueel.
    [Fact]
    public async Task Reopening_preserves_request_date_clears_reason_and_requires_a_new_session()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.ExternalInstructor,
            AccountApprovalStatus.Refused, roles: [RoleNames.Lesgever]);
        target.RefusalReason = "Oude weigering";
        await db.Context.SaveChangesAsync();
        var requested = target.AccountRequestedAtUtc;
        var stamp = target.SecurityStamp;
        Assert.True((await ReviewAsync(db.Services, actor, target, AccountReviewAction.Reopen)).Succeeded);
        var stored = await ReloadAsync(db.Context, target.Id);
        Assert.Equal(AccountApprovalStatus.Pending, stored.AccountApprovalStatus);
        Assert.Null(stored.RefusalReason);
        Assert.Equal(requested, stored.AccountRequestedAtUtc);
        Assert.NotEqual(stamp, stored.SecurityStamp);
        Assert.Equal(actor.Id, stored.ReviewedById);
        Assert.Equal(db.Services.GetRequiredService<FixedTimeProvider>().UtcNow, stored.ReviewedAt);
        Assert.Empty(await db.Services.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(stored));
        Assert.Equal(1, Notifier(db.Services).Count);
    }

    // Deze actortoestanden worden uit de huidige database gelezen, niet uit een oude principal.
    [Theory]
    [InlineData("missing")]
    [InlineData("pending")]
    [InlineData("refused")]
    [InlineData("blocked")]
    [InlineData("revoked-role")]
    public async Task Ineligible_current_actor_is_forbidden_without_any_target_mutation(string state)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.Employee);
        if (state == "pending") actor.AccountApprovalStatus = AccountApprovalStatus.Pending;
        if (state == "refused") actor.AccountApprovalStatus = AccountApprovalStatus.Refused;
        if (state == "blocked") actor.IsBlocked = true;
        if (state == "revoked-role")
            Assert.True((await db.Services.GetRequiredService<UserManager<ApplicationUser>>().RemoveFromRoleAsync(actor, RoleNames.Beheerder)).Succeeded);
        await db.Context.SaveChangesAsync();
        var result = await db.Services.GetRequiredService<AccountReviewService>().ReviewAsync(
            state == "missing" ? "missing-actor" : actor.Id,
            new AccountReviewCommand(target.Id, target.ConcurrencyStamp!, AccountReviewAction.Approve));
        Assert.Equal(AccountOperationStatus.Forbidden, result.Status);
        Assert.Equal(AccountApprovalStatus.Pending, (await ReloadAsync(db.Context, target.Id)).AccountApprovalStatus);
        Assert.Empty(await db.Context.UserRoles.Where(x => x.UserId == target.Id).ToListAsync());
        Assert.Equal(0, Notifier(db.Services).Count);
    }

    // Zelfbeoordeling wordt serverzijdig geblokkeerd, ook voor een verder bevoegde beheerder.
    [Fact]
    public async Task Administrator_cannot_review_self()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        Assert.Equal(AccountOperationStatus.Forbidden, (await ReviewAsync(db.Services, actor, actor, AccountReviewAction.Reopen)).Status);
        Assert.Equal(0, Notifier(db.Services).Count);
    }

    // Beheerdersaccounts vallen buiten deze aanvraagworkflow; de service mag hun rechten niet herschrijven.
    [Fact]
    public async Task A_target_with_administrator_role_cannot_be_reviewed()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.Employee, roles: [RoleNames.Beheerder]);
        Assert.Equal(AccountOperationStatus.Forbidden, (await ReviewAsync(db.Services, actor, target, AccountReviewAction.Approve)).Status);
        Assert.Equal(AccountApprovalStatus.Pending, (await ReloadAsync(db.Context, target.Id)).AccountApprovalStatus);
        Assert.Equal(0, Notifier(db.Services).Count);
    }

    // Een verdwenen target is een gecontroleerd resultaat, zonder melding of verzonnen beslissing.
    [Fact]
    public async Task Missing_target_returns_not_found()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var result = await db.Services.GetRequiredService<AccountReviewService>().ReviewAsync(actor.Id,
            new AccountReviewCommand("missing-target", "missing-version", AccountReviewAction.Approve));
        Assert.Equal(AccountOperationStatus.NotFound, result.Status);
        Assert.Equal(0, Notifier(db.Services).Count);
    }

    // De verwachte ConcurrencyStamp is de bewust gelezen formulier-versie; een oude versie mag niets overschrijven.
    [Fact]
    public async Task Stale_expected_version_returns_conflict_without_decision_or_roles()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.Employee);
        var stamp = target.SecurityStamp;
        var result = await db.Services.GetRequiredService<AccountReviewService>().ReviewAsync(actor.Id,
            new AccountReviewCommand(target.Id, "stale-version", AccountReviewAction.Approve));
        Assert.Equal(AccountOperationStatus.Conflict, result.Status);
        var stored = await ReloadAsync(db.Context, target.Id);
        Assert.Equal(AccountApprovalStatus.Pending, stored.AccountApprovalStatus);
        Assert.Equal(stamp, stored.SecurityStamp);
        Assert.Null(stored.ReviewedAt);
        Assert.Empty(await db.Context.UserRoles.Where(x => x.UserId == target.Id).ToListAsync());
        Assert.Equal(0, Notifier(db.Services).Count);
    }

    // Elke verboden overgang gebruikt een eigen target; geen succes van een eerdere iteratie kan dit bewijs dragen.
    [Fact]
    public async Task Wrong_state_for_each_review_action_is_a_controlled_conflict()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        foreach (var (state, action) in new[]
        {
            (AccountApprovalStatus.Approved, AccountReviewAction.Approve),
            (AccountApprovalStatus.Refused, AccountReviewAction.Refuse),
            (AccountApprovalStatus.Pending, AccountReviewAction.Reopen)
        })
        {
            var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.Employee, state);
            Assert.Equal(AccountOperationStatus.Conflict,
                (await ReviewAsync(db.Services, actor, target, action, reason: "Reden")).Status);
            Assert.Equal(state, (await ReloadAsync(db.Context, target.Id)).AccountApprovalStatus);
        }
        Assert.Equal(0, Notifier(db.Services).Count);
    }

    // Legacydata mag blijven bestaan, maar beoordeling mag ze niet stilzwijgend omzetten of repareren.
    [Theory]
    [InlineData("missing-type")]
    [InlineData("unknown-type")]
    [InlineData("missing-profile")]
    [InlineData("opposite-profile")]
    public async Task Incomplete_or_mismatched_profile_is_not_approved_or_repaired(string defect)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var type = defect == "opposite-profile" ? RequestedAccountType.Employee : RequestedAccountType.InternalInstructor;
        var target = await TestAccountData.CreateAsync(db.Services, type, profile: false);
        if (defect == "missing-type") target.RequestedAccountType = null;
        if (defect == "unknown-type") target.RequestedAccountType = (RequestedAccountType)99;
        if (defect == "opposite-profile")
            db.Context.ExternalInstructors.Add(new ExternalInstructor { ApplicationUserId = target.Id, OrganizationName = "Legacy organisatie" });
        await db.Context.SaveChangesAsync();
        var originalType = target.RequestedAccountType;
        var result = await ReviewAsync(db.Services, actor, target, AccountReviewAction.Approve,
            defect == "missing-profile" ? target.DepartmentId : null);
        Assert.Equal(AccountOperationStatus.ValidationFailure, result.Status);
        var stored = await ReloadAsync(db.Context, target.Id);
        Assert.Equal(originalType, stored.RequestedAccountType);
        Assert.Equal(AccountApprovalStatus.Pending, stored.AccountApprovalStatus);
        Assert.Empty(await db.Context.InternalInstructors.Where(x => x.ApplicationUserId == target.Id).ToListAsync());
        Assert.Equal(defect == "opposite-profile" ? 1 : 0,
            await db.Context.ExternalInstructors.CountAsync(x => x.ApplicationUserId == target.Id));
        Assert.Equal(0, Notifier(db.Services).Count);
    }

    // Extern blijft extern: een geldige afdeling kan een organisatieaanvraag niet naar een intern profiel omzetten.
    [Fact]
    public async Task External_account_cannot_receive_an_internal_department()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.ExternalInstructor);
        var department = await db.Context.Departments.Select(x => x.Id).FirstAsync();
        Assert.Equal(AccountOperationStatus.ValidationFailure,
            (await ReviewAsync(db.Services, actor, target, AccountReviewAction.Approve, department)).Status);
        Assert.Null((await ReloadAsync(db.Context, target.Id)).DepartmentId);
        Assert.Equal(0, Notifier(db.Services).Count);
    }

    // Zowel lege als te lange redenen moeten falen vóór status, reviewer of rollen wijzigen.
    [Fact]
    public async Task Refusal_requires_a_nonblank_reason_at_most_one_thousand_characters()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.Employee);
        foreach (var reason in new[] { " ", new string('x', 1001) })
        {
            Assert.Equal(AccountOperationStatus.ValidationFailure,
                (await ReviewAsync(db.Services, actor, target, AccountReviewAction.Refuse, reason: reason)).Status);
            var stored = await ReloadAsync(db.Context, target.Id);
            Assert.Equal(AccountApprovalStatus.Pending, stored.AccountApprovalStatus);
            Assert.Null(stored.RefusalReason);
            Assert.Null(stored.ReviewedAt);
        }
        Assert.Equal(0, Notifier(db.Services).Count);
    }

    // Dit is echte concurrentie: twee services, contexten en verbindingen naar één verse file-DB.
    [Fact]
    public async Task Two_independent_reviews_of_the_same_version_accept_exactly_one_decision()
    {
        await using var db = await FileSqliteTestDatabase.CreateAsync();
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.Employee);
        await using var first = db.CreateScope();
        await using var second = db.CreateScope();
        var firstContext = first.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var secondContext = second.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.NotSame(firstContext.Database.GetDbConnection(), secondContext.Database.GetDbConnection());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<AccountOperationResult> RunAsync(IServiceProvider services, AccountReviewAction action)
        {
            await start.Task;
            return await services.GetRequiredService<AccountReviewService>().ReviewAsync(actor.Id,
                new AccountReviewCommand(target.Id, target.ConcurrencyStamp!, action,
                    RefusalReason: action == AccountReviewAction.Refuse ? "Concurrente weigering" : null));
        }
        var one = Task.Run(() => RunAsync(first.ServiceProvider, AccountReviewAction.Approve));
        var two = Task.Run(() => RunAsync(second.ServiceProvider, AccountReviewAction.Refuse));
        start.SetResult();
        var results = await Task.WhenAll(one, two);
        Assert.Single(results, x => x.Status == AccountOperationStatus.Success);
        Assert.Single(results, x => x.Status == AccountOperationStatus.Conflict);
        await using var check = db.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await context.Users.SingleAsync(x => x.Id == target.Id);
        var roles = await check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(stored);
        if (stored.AccountApprovalStatus == AccountApprovalStatus.Approved)
        {
            Assert.Equal(new[] { RoleNames.Medewerker }, roles);
            Assert.Null(stored.RefusalReason);
        }
        else
        {
            Assert.Equal(AccountApprovalStatus.Refused, stored.AccountApprovalStatus);
            Assert.Equal("Concurrente weigering", stored.RefusalReason);
            Assert.Empty(roles);
        }
        Assert.Equal(actor.Id, stored.ReviewedById);
        Assert.Equal(1, Notifier(db.Services).Count);
    }

    // Een gedeeltelijk geslaagde rolgrant hoort bij dezelfde transactie als de beoordeling.
    [Fact]
    public async Task Failed_role_assignment_restores_all_review_fields_and_previous_roles()
    {
        await AssertAtomicFailureAsync(failRoles: true);
    }

    // Ook een Identity-foutresultaat zonder exception moet alle eerdere veranderingen terugdraaien.
    [Fact]
    public async Task Failed_user_update_restores_department_reviewer_stamps_status_and_roles()
    {
        await AssertAtomicFailureAsync(failRoles: false);
    }

    /// <summary>Injecteert één foutgrens en vergelijkt daarna de volledige bewaarde beslissing met een onafhankelijke read.</summary>
    private static async Task AssertAtomicFailureAsync(bool failRoles)
    {
        var failure = new ReviewFailureSwitch();
        await using var db = await FileSqliteTestDatabase.CreateAsync(configureServices: services =>
        {
            services.AddSingleton(failure);
            services.AddScoped<UserManager<ApplicationUser>, FailingReviewUserManager>();
        });
        await TestAccountData.InitializeAsync(db.Services);
        var actor = await TestAccountData.CreateAdministratorAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.InternalInstructor,
            roles: [RoleNames.Lesgever]);
        var before = await ReloadAsync(db.Context, target.Id);
        var corrected = await db.Context.Departments.Where(x => x.Id != target.DepartmentId).Select(x => x.Id).FirstAsync();
        failure.FailRoles = failRoles;
        failure.Armed = true;
        var result = await ReviewAsync(db.Services, actor, target, AccountReviewAction.Approve, corrected);
        Assert.False(result.Succeeded);
        Assert.True(failure.Triggered);
        // Onafhankelijk lezen voorkomt dat een teruggedraaid EF-object als databasebewijs geldt.
        await using var check = db.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await context.Users.SingleAsync(x => x.Id == target.Id);
        Assert.Equal(before.AccountApprovalStatus, stored.AccountApprovalStatus);
        Assert.Equal(before.DepartmentId, stored.DepartmentId);
        Assert.Equal(before.ReviewedAt, stored.ReviewedAt);
        Assert.Equal(before.ReviewedById, stored.ReviewedById);
        Assert.Equal(before.SecurityStamp, stored.SecurityStamp);
        Assert.Equal(before.ConcurrencyStamp, stored.ConcurrencyStamp);
        Assert.Equal(before.RefusalReason, stored.RefusalReason);
        Assert.Equal(before.RequestedAccountType, stored.RequestedAccountType);
        Assert.Equal(new[] { RoleNames.Lesgever },
            await check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(stored));
        Assert.Equal(1, await context.InternalInstructors.CountAsync(x => x.ApplicationUserId == target.Id));
        Assert.Equal(0, Notifier(db.Services).Count);
        // Dezelfde scope moet na de gecontroleerde fout schoon opnieuw gebruikt kunnen worden.
        failure.Armed = false;
        Assert.True((await ReviewAsync(db.Services, actor, before, AccountReviewAction.Approve, corrected)).Succeeded);
    }

    /// <summary>Geeft actor-ID en de oorspronkelijke targetversie mee; de service moet de actuele bevoegdheid zelf lezen.</summary>
    private static Task<AccountOperationResult> ReviewAsync(IServiceProvider services, ApplicationUser actor,
        ApplicationUser target, AccountReviewAction action, int? department = null, string? reason = null) =>
        services.GetRequiredService<AccountReviewService>().ReviewAsync(actor.Id,
            new AccountReviewCommand(target.Id, target.ConcurrencyStamp!, action, department, reason));

    /// <summary>AsNoTracking voorkomt dat een gemuteerd in-memory object als opgeslagen resultaat geldt.</summary>
    private static Task<ApplicationUser> ReloadAsync(ApplicationDbContext context, string id) =>
        context.Users.AsNoTracking().SingleAsync(x => x.Id == id);

    /// <summary>Leest de gedeelde meldopname van deze testcase; echte transportontvangst wordt apart getest.</summary>
    private static RecordingAccountApplicationsNotifier Notifier(IServiceProvider services) =>
        services.GetRequiredService<RecordingAccountApplicationsNotifier>();

    /// <summary>Schakelt foutinjectie pas na testvoorbereiding in en bewijst dat de bedoelde foutgrens is bereikt.</summary>
    private sealed class ReviewFailureSwitch
    {
        public bool Armed { get; set; }
        public bool FailRoles { get; set; }
        public bool Triggered { get; set; }
    }

    // Identity kan een foutresultaat geven zonder exception. Injecteer dit nadat echte stores schrijven.
    private sealed class FailingReviewUserManager(
        IUserStore<ApplicationUser> store, IOptions<IdentityOptions> options,
        IPasswordHasher<ApplicationUser> passwordHasher, IEnumerable<IUserValidator<ApplicationUser>> userValidators,
        IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators, ILookupNormalizer normalizer,
        IdentityErrorDescriber describer, IServiceProvider services, ILogger<UserManager<ApplicationUser>> logger,
        ReviewFailureSwitch failure)
        : UserManager<ApplicationUser>(store, options, passwordHasher, userValidators, passwordValidators,
            normalizer, describer, services, logger)
    {
        /// <summary>Schrijft eerst echt één rol, waarna de nagebootste Identity-fout de buitenste rollback vereist.</summary>
        public override async Task<IdentityResult> AddToRolesAsync(ApplicationUser user, IEnumerable<string> roles)
        {
            if (!failure.Armed || !failure.FailRoles) return await base.AddToRolesAsync(user, roles);
            failure.Triggered = true;
            // Een gedeeltelijke geslaagde grant moet mee verdwijnen met de buitenste rollback.
            var firstRole = roles.Take(1).ToArray();
            if (firstRole.Length != 0) await base.AddToRolesAsync(user, firstRole);
            return IdentityResult.Failed(new IdentityError { Code = "InjectedRoleFailure", Description = "Testfout." });
        }

        /// <summary>Geeft gecontroleerd een updatefout terug zodat de service het resultaat, niet alleen exceptions, moet afhandelen.</summary>
        public override Task<IdentityResult> UpdateAsync(ApplicationUser user)
        {
            if (!failure.Armed || failure.FailRoles) return base.UpdateAsync(user);
            failure.Triggered = true;
            return Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "InjectedUpdateFailure", Description = "Testfout." }));
        }
    }
}
