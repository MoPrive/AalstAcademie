using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Accounts;

/// <summary>Veertien gevallen bewijzen dat een aanvraag volledig wordt opgeslagen of geheel wordt geweigerd.</summary>
public class AccountRegistrationServiceTests
{
    // De testgrens ligt hier onder HTTP: echte Identity en SQLite controleren de businessoperatie.
    // Formulierbinding, cookies en CSRF krijgen afzonderlijk bewijs in RegistrationHttpTests.
    // Elk type krijgt uitsluitend zijn eigen gegevensvorm; een aanvraag verleent nog geen toegang.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Valid_type_creates_one_pending_trimmed_account_and_matching_profile_after_commit(int type)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var command = await ValidCommandAsync(db, (RequestedAccountType)type);
        var notifier = db.Services.GetRequiredService<RecordingAccountApplicationsNotifier>();
        var committedWhenNotified = false;
        notifier.OnNotifyAsync = async _ =>
        {
            committedWhenNotified = db.Context.Database.CurrentTransaction is null;
            Assert.Equal(1, await db.Context.Users.AsNoTracking().CountAsync());
        };
        // Act: één service-aanroep. De controles hierna lezen opnieuw uit SQLite, inclusief rollen/profielen.
        var result = await db.Services.GetRequiredService<AccountRegistrationService>().RegisterAsync(command);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.UserId);
        db.Context.ChangeTracker.Clear();
        var stored = await db.Context.Users.SingleAsync();
        Assert.Equal(result.UserId, stored.Id);
        Assert.Equal(command.Email!.Trim(), stored.Email);
        Assert.Equal(command.Email.Trim(), stored.UserName);
        Assert.Equal(command.Email.Trim().ToUpperInvariant(), stored.NormalizedEmail);
        Assert.Equal(command.PhoneNumber!.Trim(), stored.PhoneNumber);
        Assert.Equal((RequestedAccountType)type, stored.RequestedAccountType);
        Assert.Equal(AccountApprovalStatus.Pending, stored.AccountApprovalStatus);
        Assert.False(stored.IsBlocked);
        Assert.False(stored.EmailConfirmed);
        Assert.Null(stored.ReviewedAt);
        Assert.Null(stored.ReviewedById);
        Assert.Null(stored.RefusalReason);
        Assert.Null(stored.DemoSeedKey);
        Assert.Equal(db.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, stored.AccountRequestedAtUtc);
        Assert.Empty(await db.Context.UserRoles.ToListAsync());
        Assert.Equal(type == 2 ? 1 : 0, await db.Context.InternalInstructors.CountAsync());
        Assert.Equal(type == 3 ? 1 : 0, await db.Context.ExternalInstructors.CountAsync());
        if (type == 3)
        {
            var profile = await db.Context.ExternalInstructors.SingleAsync();
            Assert.Equal("Fictieve Academie", profile.OrganizationName);
            Assert.Equal("BE0123456789", profile.VatNumber);
            Assert.Null(stored.DepartmentId);
            Assert.Null(stored.FirstName);
        }
        else
        {
            Assert.Equal("Noor", stored.FirstName);
            Assert.Equal("Peeters", stored.LastName);
            Assert.Equal(command.DepartmentId, stored.DepartmentId);
            Assert.Null(stored.ManagerUserId);
        }
        Assert.True(committedWhenNotified);
        Assert.Equal(1, notifier.Count);
    }

    // Zowel een ontbrekende keuze als een gemanipuleerde enum is serverzijdig ongeldig.
    [Theory]
    [InlineData(null)]
    [InlineData(99)]
    public async Task Missing_or_unknown_type_returns_field_error_without_writes(int? type)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var command = await ValidCommandAsync(db);
        var result = await db.Services.GetRequiredService<AccountRegistrationService>().RegisterAsync(
            command with { RequestedAccountType = type is null ? null : (RequestedAccountType)type });
        Assert.Equal(AccountOperationStatus.ValidationFailure, result.Status);
        Assert.Contains(nameof(command.RequestedAccountType), result.Errors!.Keys);
        await AssertNothingCreatedAsync(db);
    }

    // De gemeenschappelijke invoer wordt ook zonder clientvalidatie gecontroleerd.
    [Theory]
    [InlineData("Email")]
    [InlineData("PhoneNumber")]
    [InlineData("ConfirmPassword")]
    public async Task Invalid_common_input_returns_correct_field_error(string field)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var command = await ValidCommandAsync(db);
        command = field switch
        {
            "Email" => command with { Email = "geen-email" },
            "PhoneNumber" => command with { PhoneNumber = "geen-telefoon" },
            _ => command with { ConfirmPassword = command.Password + "ander" }
        };
        var result = await db.Services.GetRequiredService<AccountRegistrationService>().RegisterAsync(command);
        Assert.Equal(AccountOperationStatus.ValidationFailure, result.Status);
        Assert.Contains(field, result.Errors!.Keys);
        await AssertNothingCreatedAsync(db);
    }

    // Identity houdt zelf zijn wachtwoordregels; de service vertaalt de echte fout naar het veld.
    [Fact]
    public async Task Identity_password_failure_is_returned_without_user_or_profile()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var command = await ValidCommandAsync(db, RequestedAccountType.InternalInstructor);
        var weak = new string('a', 2);
        var result = await db.Services.GetRequiredService<AccountRegistrationService>().RegisterAsync(
            command with { Password = weak, ConfirmPassword = weak });
        Assert.Equal(AccountOperationStatus.ValidationFailure, result.Status);
        Assert.Contains(nameof(command.Password), result.Errors!.Keys);
        await AssertNothingCreatedAsync(db);
    }

    // Normalisatie maakt een variant met hoofdletters en randspaties geen tweede identiteit.
    [Fact]
    public async Task Case_and_whitespace_duplicate_email_is_rejected_and_original_is_preserved()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var command = await ValidCommandAsync(db);
        var service = db.Services.GetRequiredService<AccountRegistrationService>();
        var first = await service.RegisterAsync(command);
        Assert.True(first.Succeeded);
        var second = await service.RegisterAsync(command with { Email = " " + command.Email!.Trim().ToUpperInvariant() + " " });
        Assert.Equal(AccountOperationStatus.ValidationFailure, second.Status);
        Assert.Contains(nameof(command.Email), second.Errors!.Keys);
        Assert.Equal(first.UserId, (await db.Context.Users.SingleAsync()).Id);
        Assert.Empty(await db.Context.UserRoles.ToListAsync());
        Assert.Equal(1, db.Services.GetRequiredService<RecordingAccountApplicationsNotifier>().Count);
    }

    // Een syntactisch positief nummer is onvoldoende: de afdeling moet werkelijk bestaan.
    [Fact]
    public async Task Nonexistent_department_is_rejected_before_account_creation()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var command = await ValidCommandAsync(db, RequestedAccountType.InternalInstructor);
        var result = await db.Services.GetRequiredService<AccountRegistrationService>().RegisterAsync(
            command with { DepartmentId = int.MaxValue });
        Assert.Equal(AccountOperationStatus.ValidationFailure, result.Status);
        Assert.Contains(nameof(command.DepartmentId), result.Errors!.Keys);
        await AssertNothingCreatedAsync(db);
    }

    // Een interne lesgever heeft geen leidinggevende; een externe heeft geen interne persoonsvelden.
    [Theory]
    [InlineData(2, "ManagerUserId")]
    [InlineData(3, "FirstName")]
    public async Task Irrelevant_fields_are_rejected_instead_of_silently_discarded(int type, string field)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var command = await ValidCommandAsync(db, (RequestedAccountType)type);
        command = type == 2 ? command with { ManagerUserId = "Ongepaste input" } : command with { FirstName = "Ongepaste input" };
        var result = await db.Services.GetRequiredService<AccountRegistrationService>().RegisterAsync(command);
        Assert.Equal(AccountOperationStatus.ValidationFailure, result.Status);
        Assert.Contains(field, result.Errors!.Keys);
        await AssertNothingCreatedAsync(db);
    }

    // De user bestaat al in dezelfde transactie als profielaanmaak faalt; rollback moet beide wissen.
    [Fact]
    public async Task Profile_conflict_rolls_back_account_and_allows_clean_retry_without_notification()
    {
        var competingWriter = new ConflictingProfileInsert();
        await using var db = await SqliteTestDatabase.CreateAsync(interceptor: competingWriter);
        var command = await ValidCommandAsync(db, RequestedAccountType.InternalInstructor);
        var service = db.Services.GetRequiredService<AccountRegistrationService>();
        competingWriter.Armed = true;
        var result = await service.RegisterAsync(command);
        Assert.Equal(AccountOperationStatus.Conflict, result.Status);
        Assert.True(competingWriter.Injected);
        await AssertNothingCreatedAsync(db);
        Assert.DoesNotContain(db.Context.ChangeTracker.Entries(), x => x.Entity is ApplicationUser or InternalInstructor or ExternalInstructor);
        Assert.Null(db.Context.Database.CurrentTransaction);
        Assert.True((await service.RegisterAsync(command)).Succeeded);
        Assert.Equal(1, await db.Context.Users.CountAsync());
        Assert.Equal(1, await db.Context.InternalInstructors.CountAsync());
        Assert.Equal(1, db.Services.GetRequiredService<RecordingAccountApplicationsNotifier>().Count);
    }

    /// <summary>Alle testgegevens zijn fictief; een wachtwoord wordt per test tijdelijk gegenereerd.</summary>
    private static async Task<AccountRegistrationCommand> ValidCommandAsync(SqliteTestDatabase db,
        RequestedAccountType type = RequestedAccountType.Employee)
    {
        var external = type == RequestedAccountType.ExternalInstructor;
        var department = new Department { Name = "Testafdeling " + Guid.NewGuid().ToString("N") };
        if (!external) { db.Context.Departments.Add(department); await db.Context.SaveChangesAsync(); }
        var password = "Aa1!" + Guid.NewGuid().ToString("N");
        return new AccountRegistrationCommand
        {
            RequestedAccountType = type, Email = "  aanvrager-" + Guid.NewGuid().ToString("N") + "@example.invalid  ",
            PhoneNumber = "  +32 490 00 00 00  ", Password = password, ConfirmPassword = password,
            FirstName = external ? null : "  Noor  ", LastName = external ? null : "  Peeters  ",
            DepartmentId = external ? null : department.Id,
            ManagerUserId = null,
            OrganizationName = external ? "  Fictieve Academie  " : null, VatNumber = external ? "  BE0123456789  " : null
        };
    }

    /// <summary>De afdelingskeuze mag blijven bestaan, maar account, profielen, rechten en melding niet.</summary>
    private static async Task AssertNothingCreatedAsync(SqliteTestDatabase db)
    {
        Assert.Empty(await db.Context.Users.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Context.InternalInstructors.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Context.ExternalInstructors.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Context.UserRoles.ToListAsync());
        Assert.Equal(0, db.Services.GetRequiredService<RecordingAccountApplicationsNotifier>().Count);
    }

    /// <summary>Veroorzaakt een echte UNIQUE-overtreding tussen profielcontrole en profielinsert.</summary>
    private sealed class ConflictingProfileInsert : SaveChangesInterceptor
    {
        // Armed voorkomt foutinjectie tijdens voorbereiding; Injected voorkomt een vals groen foutpad.
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        /// <summary>Voegt vóór EF-insert een dubbele FK in dezelfde transactie in; de UNIQUE-index veroorzaakt rollback.</summary>
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
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
}
