using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Hubs;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AalstAcademie.Tests.Realtime;

/// <summary>
/// Zeven gevallen toetsen de echte notifier rond de commitgrens. Alleen het transport wordt opgenomen;
/// onafhankelijke SQLite-verbindingen bewijzen dat de gemelde gegevens daadwerkelijk zijn opgeslagen.
/// </summary>
public class AccountApplicationsNotificationTests
{
    // Bij ontvangst moeten user én profiel al duurzaam bestaan, terwijl rollen voor Pending nog ontbreken.
    [Fact]
    public async Task Registration_notifies_only_current_administrators_after_account_and_profile_commit()
    {
        var hub = new CapturingHubContext();
        await using var db = await CreateDatabaseAsync(hub);
        var administrators = await PrepareRecipientsAsync(db.Services);
        var command = await RegistrationCommandAsync(db.Services);
        StoredAccount? observed = null;
        hub.Proxy.OnSendAsync = async () =>
        {
            // Een tweede context opent een eigen verbinding: EF-tracking is geen commitbewijs.
            AssertNoTransactionAtNotification(db, hub);
            observed = await ReadStoredAsync(db, email: command.Email);
        };

        var result = await db.Services.GetRequiredService<AccountRegistrationService>().RegisterAsync(command);

        Assert.True(result.Succeeded);
        AssertNoReadFailure(hub);
        Assert.NotNull(observed);
        Assert.Equal(result.UserId, observed.User.Id);
        Assert.Equal(AccountApprovalStatus.Pending, observed.User.AccountApprovalStatus);
        Assert.NotNull(observed.User.PasswordHash);
        Assert.Equal(1, observed.InternalProfiles);
        Assert.Equal(0, observed.ExternalProfiles);
        Assert.Empty(observed.Roles);
        AssertSinglePayloadlessInvalidation(hub, administrators);
    }

    // Elke overgang krijgt een eigen database en laat tijdens het event de volledige nieuwe toestand zien.
    [Theory]
    [InlineData(AccountReviewAction.Approve)]
    [InlineData(AccountReviewAction.Refuse)]
    [InlineData(AccountReviewAction.Reopen)]
    public async Task Review_notifies_after_metadata_roles_and_session_stamp_commit(AccountReviewAction action)
    {
        var hub = new CapturingHubContext();
        await using var db = await CreateDatabaseAsync(hub);
        var administrators = await PrepareRecipientsAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.InternalInstructor,
            action == AccountReviewAction.Reopen ? AccountApprovalStatus.Refused : AccountApprovalStatus.Pending,
            roles: [RoleNames.Medewerker, RoleNames.Lesgever]);
        target.RefusalReason = action == AccountReviewAction.Reopen ? "Een eerdere fictieve weigering." : null;
        await db.Context.SaveChangesAsync();
        var originalStamp = target.SecurityStamp;
        var originalVersion = target.ConcurrencyStamp;
        var correctedDepartment = await db.Context.Departments.Where(x => x.Id != target.DepartmentId)
            .Select(x => x.Id).FirstAsync();
        StoredAccount? observed = null;
        hub.Proxy.OnSendAsync = async () =>
        {
            AssertNoTransactionAtNotification(db, hub);
            observed = await ReadStoredAsync(db, id: target.Id);
        };

        var result = await db.Services.GetRequiredService<AccountReviewService>().ReviewAsync(administrators[0],
            new AccountReviewCommand(target.Id, originalVersion!, action,
                action == AccountReviewAction.Approve ? correctedDepartment : null,
                action == AccountReviewAction.Refuse ? "  De afdeling is nog niet bevestigd.  " : null));

        Assert.True(result.Succeeded);
        AssertNoReadFailure(hub);
        Assert.NotNull(observed);
        var expectedStatus = action switch
        {
            AccountReviewAction.Approve => AccountApprovalStatus.Approved,
            AccountReviewAction.Refuse => AccountApprovalStatus.Refused,
            _ => AccountApprovalStatus.Pending
        };
        Assert.Equal(expectedStatus, observed.User.AccountApprovalStatus);
        Assert.Equal(administrators[0], observed.User.ReviewedById);
        Assert.Equal(db.Services.GetRequiredService<TimeProvider>().GetUtcNow(), observed.User.ReviewedAt);
        Assert.NotEqual(originalStamp, observed.User.SecurityStamp);
        Assert.NotEqual(originalVersion, observed.User.ConcurrencyStamp);
        Assert.Equal(target.AccountRequestedAtUtc, observed.User.AccountRequestedAtUtc);
        Assert.Equal(action == AccountReviewAction.Refuse ? "De afdeling is nog niet bevestigd." : null,
            observed.User.RefusalReason);
        Assert.Equal(action == AccountReviewAction.Approve ? correctedDepartment : target.DepartmentId,
            observed.User.DepartmentId);
        Assert.Equal(1, observed.InternalProfiles);
        Assert.Equal(0, observed.ExternalProfiles);
        Assert.Equal(action == AccountReviewAction.Approve
            ? new[] { RoleNames.Lesgever, RoleNames.Medewerker } : Array.Empty<string>(), observed.Roles);
        AssertSinglePayloadlessInvalidation(hub, administrators);
    }

    // Een echte UNIQUE-overtreding verplaatst de registratie naar rollback; ontvangerselectie/publicatie mogen niet starten.
    [Fact]
    public async Task Registration_rollback_after_real_profile_constraint_failure_emits_no_invalidation()
    {
        var hub = new CapturingHubContext();
        var failure = new ConflictingProfileInsert();
        await using var db = await CreateDatabaseAsync(hub, interceptor: failure);
        await PrepareRecipientsAsync(db.Services);
        var command = await RegistrationCommandAsync(db.Services);
        failure.Armed = true;

        var result = await db.Services.GetRequiredService<AccountRegistrationService>().RegisterAsync(command);

        Assert.Equal(AccountOperationStatus.Conflict, result.Status);
        Assert.True(failure.Injected);
        // Lees het eindresultaat buiten de schrijvende scope; de ingevoegde user én het profiel zijn verdwenen.
        await using var read = db.CreateScope();
        var context = read.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await context.Users.AnyAsync(x => x.Email == command.Email));
        Assert.Empty(await context.InternalInstructors.AsNoTracking().ToListAsync());
        Assert.Empty(await context.ExternalInstructors.AsNoTracking().ToListAsync());
        Assert.Null(db.Context.Database.CurrentTransaction);
        Assert.Empty(hub.Proxy.Events);
        Assert.Empty(hub.RecipientSelections);
    }

    // Na echte rol- en user-writes moet een late fout zowel die writes als elk invalidatiesignaal terugdraaien.
    [Fact]
    public async Task Review_rollback_after_real_role_and_user_writes_emits_no_invalidation()
    {
        var hub = new CapturingHubContext();
        var failure = new ReviewFailureSwitch();
        await using var db = await CreateDatabaseAsync(hub, configure: services =>
        {
            services.AddSingleton(failure);
            services.AddScoped<UserManager<ApplicationUser>, FailingReviewUserManager>();
        });
        var administrators = await PrepareRecipientsAsync(db.Services);
        var target = await TestAccountData.CreateAsync(db.Services, RequestedAccountType.InternalInstructor,
            roles: [RoleNames.Lesgever]);
        var before = (await ReadStoredAsync(db, id: target.Id))!;
        var correctedDepartment = await db.Context.Departments.Where(x => x.Id != target.DepartmentId)
            .Select(x => x.Id).FirstAsync();
        failure.Armed = true;

        var result = await db.Services.GetRequiredService<AccountReviewService>().ReviewAsync(administrators[0],
            new AccountReviewCommand(target.Id, target.ConcurrencyStamp!, AccountReviewAction.Approve,
                correctedDepartment));

        Assert.False(result.Succeeded);
        Assert.True(failure.Triggered);
        var after = (await ReadStoredAsync(db, id: target.Id))!;
        Assert.Equal(before.User.AccountApprovalStatus, after.User.AccountApprovalStatus);
        Assert.Equal(before.User.DepartmentId, after.User.DepartmentId);
        Assert.Equal(before.User.ReviewedAt, after.User.ReviewedAt);
        Assert.Equal(before.User.ReviewedById, after.User.ReviewedById);
        Assert.Equal(before.User.SecurityStamp, after.User.SecurityStamp);
        Assert.Equal(before.User.ConcurrencyStamp, after.User.ConcurrencyStamp);
        Assert.Equal(before.Roles, after.Roles);
        Assert.Equal(before.InternalProfiles, after.InternalProfiles);
        Assert.Null(db.Context.Database.CurrentTransaction);
        Assert.Empty(hub.Proxy.Events);
        Assert.Empty(hub.RecipientSelections);
    }

    // Publicatie volgt ná commit: een transportfout mag een opgeslagen beslissing niet als mislukte businesswrite melden.
    [Fact]
    public async Task Transport_failure_keeps_registration_and_review_success_durable()
    {
        var hub = new CapturingHubContext();
        await using var db = await CreateDatabaseAsync(hub);
        var administrators = await PrepareRecipientsAsync(db.Services);
        var command = await RegistrationCommandAsync(db.Services);
        // Geen time-out of sleep: de transportgrens faalt deterministisch nadat de service heeft gecommit.
        hub.Proxy.ExceptionToThrow = new IOException("Fictieve testtransportfout.");
        var registration = await db.Services.GetRequiredService<AccountRegistrationService>().RegisterAsync(command);
        Assert.True(registration.Succeeded);
        var registered = (await ReadStoredAsync(db, id: registration.UserId))!;
        Assert.Equal(AccountApprovalStatus.Pending, registered.User.AccountApprovalStatus);
        Assert.Equal(1, registered.InternalProfiles);
        Assert.Empty(registered.Roles);

        var review = await db.Services.GetRequiredService<AccountReviewService>().ReviewAsync(administrators[0],
            new AccountReviewCommand(registered.User.Id, registered.User.ConcurrencyStamp!, AccountReviewAction.Approve));

        Assert.True(review.Succeeded);
        var approved = (await ReadStoredAsync(db, id: registration.UserId))!;
        Assert.Equal(AccountApprovalStatus.Approved, approved.User.AccountApprovalStatus);
        Assert.Equal(administrators[0], approved.User.ReviewedById);
        Assert.NotEqual(registered.User.SecurityStamp, approved.User.SecurityStamp);
        Assert.Equal(new[] { RoleNames.Lesgever, RoleNames.Medewerker }, approved.Roles);
        Assert.Equal(1, approved.InternalProfiles);
        Assert.Equal(2, hub.Proxy.Events.Count);
        Assert.All(hub.Proxy.Events, message =>
        {
            Assert.Equal("AccountApplicationsChanged", message.Method);
            Assert.Empty(message.Arguments);
        });
        Assert.All(hub.RecipientSelections, recipients => Assert.Equal(administrators.Order(), recipients.Order()));
    }

    /// <summary>Vervangt uitsluitend de transportgrens; productiequery en echte Identity/SQLite blijven actief.</summary>
    private static Task<FileSqliteTestDatabase> CreateDatabaseAsync(CapturingHubContext hub,
        IInterceptor? interceptor = null, Action<IServiceCollection>? configure = null) =>
        FileSqliteTestDatabase.CreateAsync(interceptor: interceptor, configureServices: services =>
        {
            services.AddSingleton(new DemoMode(false));
            services.AddSingleton<IHubContext<AccountApplicationsHub>>(hub);
            services.AddScoped<IAccountApplicationNotifier, SignalRAccountApplicationNotifier>();
            configure?.Invoke(services);
        });

    /// <summary>
    /// Alleen de eerste twee gebruikers zijn geldige ontvangers. De overige fictieve accounts bewijzen
    /// dat pending, refused, blokkering, ingetrokken rol, deelnemerrol en demo buiten demo worden uitgesloten.
    /// </summary>
    private static async Task<string[]> PrepareRecipientsAsync(IServiceProvider services)
    {
        await TestAccountData.InitializeAsync(services);
        var first = await TestAccountData.CreateAdministratorAsync(services);
        var second = await TestAccountData.CreateAdministratorAsync(services);
        await TestAccountData.CreateAsync(services, null, AccountApprovalStatus.Pending, roles: [RoleNames.Beheerder]);
        await TestAccountData.CreateAsync(services, null, AccountApprovalStatus.Refused, roles: [RoleNames.Beheerder]);
        await TestAccountData.CreateAsync(services, null, AccountApprovalStatus.Approved, blocked: true,
            roles: [RoleNames.Beheerder]);
        var revoked = await TestAccountData.CreateAdministratorAsync(services);
        var removal = await services.GetRequiredService<UserManager<ApplicationUser>>()
            .RemoveFromRoleAsync(revoked, RoleNames.Beheerder);
        Assert.True(removal.Succeeded);
        await TestAccountData.CreateAsync(services, RequestedAccountType.Employee, AccountApprovalStatus.Approved,
            roles: [RoleNames.Medewerker]);
        var demo = await TestAccountData.CreateAdministratorAsync(services);
        demo.DemoSeedKey = "test.notification.demo-admin";
        await services.GetRequiredService<ApplicationDbContext>().SaveChangesAsync();
        return [first.Id, second.Id];
    }

    /// <summary>Een interne aanvraag vereist zowel een user-write als profiel-write en vormt daardoor een sterke commitproef.</summary>
    private static async Task<AccountRegistrationCommand> RegistrationCommandAsync(IServiceProvider services)
    {
        var department = await services.GetRequiredService<ApplicationDbContext>().Departments.OrderBy(x => x.Id)
            .Select(x => x.Id).FirstAsync();
        var password = "Aa1!" + Guid.NewGuid().ToString("N");
        return new AccountRegistrationCommand
        {
            RequestedAccountType = RequestedAccountType.InternalInstructor,
            Email = $"notification-{Guid.NewGuid():N}@example.invalid", PhoneNumber = "0490000000",
            Password = password, ConfirmPassword = password, FirstName = "Noor", LastName = "Test",
            DepartmentId = department
        };
    }

    /// <summary>Iedere aanroep leest via een afzonderlijke DbContext en fysieke SQLite-verbinding.</summary>
    private static async Task<StoredAccount?> ReadStoredAsync(FileSqliteTestDatabase db,
        string? id = null, string? email = null)
    {
        await using var read = db.CreateScope();
        var context = read.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.NotSame(db.Context.Database.GetDbConnection(), context.Database.GetDbConnection());
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(x => id != null ? x.Id == id : x.Email == email);
        if (user is null) return null;
        var roles = await (from link in context.UserRoles
            join role in context.Roles on link.RoleId equals role.Id
            where link.UserId == user.Id
            orderby role.Name
            select role.Name!).ToArrayAsync();
        return new StoredAccount(user, roles,
            await context.InternalInstructors.CountAsync(x => x.ApplicationUserId == user.Id),
            await context.ExternalInstructors.CountAsync(x => x.ApplicationUserId == user.Id));
    }

    // Een assertion in een notifiercallback zou door best-effort dispatch worden opgevangen.
    // Bewaar daarom diagnostiek en toets die expliciet buiten de serviceaanroep.
    private static void AssertNoTransactionAtNotification(FileSqliteTestDatabase db, CapturingHubContext hub) =>
        hub.TransactionWasOpen |= db.Context.Database.CurrentTransaction is not null;

    /// <summary>Controleert buiten best-effort dispatch of de opslagobservatie slaagde en pas na commit werd uitgevoerd.</summary>
    private static void AssertNoReadFailure(CapturingHubContext hub)
    {
        Assert.False(hub.TransactionWasOpen);
        Assert.Null(hub.Proxy.ReadException);
    }

    /// <summary>Eén lege eventpayload en uitsluitend actuele beheerder-ID's begrenzen hoeveelheid en gegevensdeling.</summary>
    private static void AssertSinglePayloadlessInvalidation(CapturingHubContext hub, string[] administrators)
    {
        var message = Assert.Single(hub.Proxy.Events);
        Assert.Equal("AccountApplicationsChanged", message.Method);
        Assert.Empty(message.Arguments);
        Assert.Equal(administrators.Order(), Assert.Single(hub.RecipientSelections).Order());
    }

    /// <summary>Onafhankelijk gelezen account, rollen en aantallen vormen één controlebeeld van de committed businessoperatie.</summary>
    private sealed record StoredAccount(ApplicationUser User, string[] Roles, int InternalProfiles, int ExternalProfiles);
    /// <summary>Neemt de protocolnaam en argumenten op; de test verwacht nul persoonsgegevens in de payload.</summary>
    private sealed record CapturedEvent(string Method, object?[] Arguments);

    /// <summary>Neemt uitsluitend serverpublicaties op; echte hubverbindingen worden elders afzonderlijk getest.</summary>
    private sealed class CapturingHubContext : IHubContext<AccountApplicationsHub>, IHubClients
    {
        public CapturingClientProxy Proxy { get; } = new();
        public List<string[]> RecipientSelections { get; } = [];
        public bool TransactionWasOpen { get; set; }
        IHubClients IHubContext<AccountApplicationsHub>.Clients => this;
        IGroupManager IHubContext<AccountApplicationsHub>.Groups => throw new NotSupportedException("Deze notifier gebruikt geen groepen.");
        /// <summary>Legt de exact geselecteerde gebruikers vast, zonder zelf hun bevoegdheid na te bootsen.</summary>
        public IClientProxy Users(IReadOnlyList<string> userIds)
        {
            RecipientSelections.Add(userIds.ToArray());
            return Proxy;
        }
        // Een onverwachte brede broadcast moet de test laten falen; zij mag geen stille fake-success geven.
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
    }

    /// <summary>Vervangt alleen de sendgrens en kan daar een controle-read of bewuste transportfout uitvoeren.</summary>
    private sealed class CapturingClientProxy : IClientProxy
    {
        public List<CapturedEvent> Events { get; } = [];
        public Func<Task>? OnSendAsync { get; set; }
        public Exception? ExceptionToThrow { get; set; }
        public Exception? ReadException { get; private set; }
        /// <summary>Kopieert eventargumenten en bewaart callbackfouten, zodat best-effort opvang geen vals groene test geeft.</summary>
        public async Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Events.Add(new CapturedEvent(method, args.ToArray()));
            if (OnSendAsync is not null)
            {
                try { await OnSendAsync(); }
                catch (Exception exception) { ReadException = exception; throw; }
            }
            if (ExceptionToThrow is not null) throw ExceptionToThrow;
        }
    }

    /// <summary>Voegt een echt concurrerend profiel in vlak vóór de profielinsert van de registratie.</summary>
    private sealed class ConflictingProfileInsert : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        /// <summary>Injecteert één duplicate-profielinsert in de open registratietransactie; rollback moet beide inserts wissen.</summary>
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            var context = eventData.Context!;
            var profile = context.ChangeTracker.Entries<InternalInstructor>()
                .SingleOrDefault(x => x.State == EntityState.Added)?.Entity;
            if (profile is null) return result;
            Armed = false;
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO InternalInstructors (ApplicationUserId) VALUES ({profile.ApplicationUserId})", cancellationToken);
            Injected = true;
            return result;
        }
    }

    /// <summary>Activeert de late updatefout pas nadat alle testaccounts en rechten zijn voorbereid.</summary>
    private sealed class ReviewFailureSwitch
    {
        public bool Armed { get; set; }
        public bool Triggered { get; set; }
    }

    /// <summary>Geeft pas na een echte user-update een foutresultaat, zodat rollback van alle writes nodig is.</summary>
    private sealed class FailingReviewUserManager(IUserStore<ApplicationUser> store, IOptions<IdentityOptions> options,
        IPasswordHasher<ApplicationUser> passwordHasher, IEnumerable<IUserValidator<ApplicationUser>> userValidators,
        IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators, ILookupNormalizer normalizer,
        IdentityErrorDescriber describer, IServiceProvider services, ILogger<UserManager<ApplicationUser>> logger,
        ReviewFailureSwitch failure)
        : UserManager<ApplicationUser>(store, options, passwordHasher, userValidators, passwordValidators,
            normalizer, describer, services, logger)
    {
        /// <summary>De echte store schrijft eerst; het daarna teruggegeven foutresultaat vereist volledige rollback door de service.</summary>
        public override async Task<IdentityResult> UpdateAsync(ApplicationUser user)
        {
            var update = await base.UpdateAsync(user);
            if (!failure.Armed || !update.Succeeded) return update;
            failure.Triggered = true;
            return IdentityResult.Failed(new IdentityError { Code = "InjectedPostWriteFailure", Description = "Testfout." });
        }
    }
}
