// Sprint 003: Bouwt fictieve owners, opleidingen, momenten en historie op en leest opslagdigests
// De gecoördineerde registratiewriters bestaan alleen in tests voor racebewijs, niet als toekomstige product-API.
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Fictieve trainingsvoorbereiding en volledige onafhankelijke opslagdigests, zonder live database of wachtwoorden.</summary>
public static class TrainingTestData
{
    public static Task InitializeAsync(IServiceProvider services) => TestAccountData.InitializeAsync(services);
    public static Task<ApplicationUser> CreateAdministratorAsync(IServiceProvider services) => TestAccountData.CreateAdministratorAsync(services);
    public static Task<ApplicationUser> CreateOwnerAsync(IServiceProvider services, bool external = false) =>
        TestAccountData.CreateAsync(services, external ? RequestedAccountType.ExternalInstructor : RequestedAccountType.InternalInstructor,
            AccountApprovalStatus.Approved, roles: external ? [RoleNames.Lesgever] : [RoleNames.Medewerker, RoleNames.Lesgever]);

    /// <summary>Dit is directe testsetup; categorie-autorisatie wordt afzonderlijk via de echte service getoetst.</summary>
    public static async Task<Category> CreateCategoryAsync(IServiceProvider services, string name = "Fictieve categorie")
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        var validation = TrainingValueValidation.ValidateCategoryName(name);
        if (!validation.Succeeded) throw new InvalidOperationException("Ongeldige fictieve categorienaam.");
        var existing = await context.Categories.SingleOrDefaultAsync(x => EF.Property<string>(x, "NormalizedName") == validation.NormalizedName);
        if (existing is not null) return existing;
        var category = new Category { Name = validation.Name! };
        context.Categories.Add(category);
        context.Entry(category).Property<string>("NormalizedName").CurrentValue = validation.NormalizedName!;
        context.Entry(category).Property<Guid>("Version").CurrentValue = Guid.NewGuid();
        await context.SaveChangesAsync();
        return category;
    }

    public static async Task<TrainingFields> FieldsAsync(IServiceProvider services, bool external = false)
    {
        var category = await CreateCategoryAsync(services);
        return new("Fictieve opleiding", "Fictieve toelichting", category.Id, external ? 12.30m : null, false);
    }

    /// <summary>Geldige records lopen via echte Create; vervolgens lezen we opnieuw zonder tracking.</summary>
    public static async Task<TrainingEntity> CreateTrainingAsync(IServiceProvider services, ApplicationUser owner,
        ApplicationUser? actor = null, TrainingAudienceScope scope = TrainingAudienceScope.Unassigned, IReadOnlyList<int>? departments = null)
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        actor ??= owner;
        var admin = await (from role in context.Roles.AsNoTracking() join link in context.UserRoles.AsNoTracking()
            on role.Id equals link.RoleId where link.UserId == actor.Id && role.Name == RoleNames.Beheerder select role.Id).AnyAsync();
        var fields = await FieldsAsync(services, owner.RequestedAccountType == RequestedAccountType.ExternalInstructor);
        var result = await services.GetRequiredService<TrainingManagementService>().CreateAsync(actor.Id,
            new(fields, admin ? owner.Id : null));
        if (!result.Succeeded || result.Id is null)
            throw new InvalidOperationException("De echte service kon de fictieve opleiding niet voorbereiden: " + result.Status);
        var row = await context.Trainings.AsNoTracking().Include(x => x.TrainingDepartments)
            .Include(x => x.Instructor).SingleAsync(x => x.Id == result.Id);
        if (scope != TrainingAudienceScope.Unassigned)
        {
            // Zelfs fixture-aanmaak kiest doelgroep via de aparte echte admincommand, nooit bij Trainingcreate.
            var audienceAdmin = admin ? actor : await CreateAdministratorAsync(services);
            var assigned = await services.GetRequiredService<TrainingManagementService>().AssignAudienceAsync(audienceAdmin.Id,
                new(row.Id, row.Version, scope, departments ?? []));
            if (!assigned.Succeeded) throw new InvalidOperationException("Fictieve doelgroep kon niet worden voorbereid.");
            row = await context.Trainings.AsNoTracking().Include(x => x.TrainingDepartments).Include(x => x.Instructor).SingleAsync(x => x.Id == row.Id);
        }
        return row;
    }

    /// <summary>De echte beheerderdienst maakt een geldige zaal; fixtureparameters zijn geen publieke controllerbinding.</summary>
    public static async Task<Location> CreateLocationAsync(IServiceProvider services, string name = "Fictieve zaal",
        int maximumCapacity = 24, string? address = null)
    {
        var admin = await CreateAdministratorAsync(services);
        var result = await services.GetRequiredService<LocationManagementService>().CreateAsync(admin.Id, new(new(name, address, maximumCapacity)));
        if (!result.Succeeded) throw new InvalidOperationException("Fictieve zaal kon niet worden voorbereid.");
        return await services.GetRequiredService<ApplicationDbContext>().Locations.AsNoTracking().SingleAsync(x => x.Id == result.Id);
    }

    /// <summary>Een uitvoering loopt via de echte createguards; alleen testcode mag een Cancelled-historievariant voorbereiden.</summary>
    public static async Task<TrainingMoment> CreateMomentAsync(IServiceProvider services, TrainingEntity training,
        ApplicationUser? actor = null, Location? location = null, DateOnly? date = null, TimeOnly? start = null,
        TimeOnly? end = null, int maximumParticipants = 12, TrainingMomentStatus status = TrainingMomentStatus.Scheduled)
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        actor ??= await context.Users.AsNoTracking().SingleAsync(x => x.Id == training.InstructorUserId);
        location ??= await CreateLocationAsync(services);
        var result = await services.GetRequiredService<TrainingMomentManagementService>().CreateAsync(actor.Id,
            new(training.Id, new(date ?? new DateOnly(2026, 12, 15), start ?? new TimeOnly(14, 0), end ?? new TimeOnly(16, 0), location.Id, maximumParticipants)));
        if (!result.Succeeded) throw new InvalidOperationException("Fictief moment kon niet worden voorbereid: " + result.Status);
        if (status != TrainingMomentStatus.Scheduled)
        {
            context.ChangeTracker.Clear();
            var tracked = await context.TrainingMoments.SingleAsync(x => x.Id == result.Id);
            tracked.Status = status;
            tracked.Version = Guid.NewGuid();
            await context.SaveChangesAsync();
        }
        return await context.TrainingMoments.AsNoTracking().Include(x => x.Training).Include(x => x.Location).SingleAsync(x => x.Id == result.Id);
    }

    /// <summary>Echte opslagrij voor guardtests; hiermee wordt geen aanvraag-/reviewworkflow geleverd.</summary>
    public static async Task<Registration> CreateRegistrationAsync(IServiceProvider services, TrainingMoment moment,
        RegistrationStatus status = RegistrationStatus.Requested, ApplicationUser? applicant = null)
    {
        applicant ??= await TestAccountData.CreateAsync(services, RequestedAccountType.Employee,
            AccountApprovalStatus.Approved, roles: [RoleNames.Medewerker]);
        var context = services.GetRequiredService<ApplicationDbContext>();
        var registration = new Registration { TrainingMomentId = moment.Id, ApplicationUserId = applicant.Id,
            Status = status, RequestedAtUtc = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
            Motivation = "Fictieve motivatie", Version = Guid.NewGuid() };
        context.Registrations.Add(registration);
        await context.SaveChangesAsync();
        return registration;
    }

    /// <summary>Actieve/gesloten historie is een echte providerfixture, zonder toekomstige promotie-API.</summary>
    public static async Task<WaitlistEntry> CreateWaitlistEntryAsync(IServiceProvider services, TrainingEntity training,
        bool closed = false, ApplicationUser? applicant = null)
    {
        applicant ??= await TestAccountData.CreateAsync(services, RequestedAccountType.Employee,
            AccountApprovalStatus.Approved, roles: [RoleNames.Medewerker]);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var entry = new WaitlistEntry { TrainingId = training.Id, ApplicationUserId = applicant.Id,
            Motivation = "Fictieve bufferreden", JoinedAtUtc = now, ClosedAtUtc = closed ? now : null, Version = Guid.NewGuid() };
        var context = services.GetRequiredService<ApplicationDbContext>();
        context.WaitlistEntries.Add(entry);
        await context.SaveChangesAsync();
        return entry;
    }

    /// <summary>
    /// Alleen concurrency-testsetup: echte historie wordt onder dezelfde vroege write-lock geschreven.
    /// Dit levert geen aanvraag-/promotieflow of beslissing over heraanvragen voor sprint004/005.
    /// </summary>
    public static Task<TrainingOperationResult> WriteFoundationCoordinatedAsync(IServiceProvider services,
        int trainingId, int? momentId, ApplicationUser applicant, Guid? expectedTermsVersion = null) =>
        services.GetRequiredService<TrainingWriteTransaction>().ExecuteAsync(async token =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            // De volledige huidige definitie leest ook voorwaarden/doelgroep; een expliciete oude voorwaardenlezing is optioneel.
            var training = await db.Trainings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainingId, token);
            if (training is null || (expectedTermsVersion is not null && training.Version != expectedTermsVersion))
                return new(TrainingOperationStatus.Conflict);
            if (momentId is null)
            {
                var row = await CreateWaitlistEntryAsync(services, training, applicant: applicant);
                return new(TrainingOperationStatus.Success, row.Id);
            }
            var moment = await db.TrainingMoments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == momentId && x.TrainingId == trainingId, token);
            if (moment is null) return new(TrainingOperationStatus.Conflict);
            var occupied = await db.Registrations.CountAsync(x => x.TrainingMomentId == momentId &&
                (x.Status == RegistrationStatus.Requested || x.Status == RegistrationStatus.Confirmed), token);
            if (occupied >= moment.MaximumParticipants) return new(TrainingOperationStatus.Conflict);
            var registration = await CreateRegistrationAsync(services, moment, applicant: applicant);
            return new(TrainingOperationStatus.Success, registration.Id);
        });

    /// <summary>
    /// Leest alle opgeslagen waarden uit een door de caller gekozen onafhankelijke scope.
    /// Alleen de SHA256 komt terug: een falende vergelijking schrijft geen accountdata/wachtwoordhashen naar output.
    /// </summary>
    public static async Task<string> DigestAsync(IServiceProvider services, bool includeFeatures = true)
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        var connection = context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync();
        try
        {
            string[] identityTables = ["AspNetUsers", "AspNetRoles", "AspNetUserRoles", "AspNetUserClaims", "AspNetRoleClaims",
                "AspNetUserLogins", "AspNetUserTokens", "Departments", "InternalInstructors", "ExternalInstructors"];
            string[] tables = includeFeatures ? [.. identityTables, "Categories", "Trainings", "TrainingDepartments",
                "Locations", "TrainingMoments", "Registrations", "WaitlistEntries"] : identityTables;
            var snapshot = new SortedDictionary<string, List<string?[]>>(StringComparer.Ordinal);
            foreach (var table in tables)
            {
                // Tabellenamen zijn een gesloten lijst van testcode, nooit gebruikersinvoer.
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM \"" + table + "\" ORDER BY 1,2";
                await using var reader = await command.ExecuteReaderAsync();
                var rows = new List<string?[]>();
                while (await reader.ReadAsync())
                {
                    var values = new string?[reader.FieldCount];
                    for (var column = 0; column < reader.FieldCount; column++)
                        values[column] = reader.IsDBNull(column) ? null : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture);
                    rows.Add(values);
                }
                snapshot.Add(table, rows);
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot))));
        }
        finally { if (opened) await connection.CloseAsync(); }
    }
}
