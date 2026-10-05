// Sprint 003: Controleert namen, Unicode-normalisatie, uniciteit, oorspronkelijke versie, rollback en directe serviceautorisatie
// Een lesgever mag ook zonder HTTP geen categorie wijzigen.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Training;

/// <summary>Veertien varianten toetsen categorievalidatie, CAS, rollback en directe serviceautorisatie.</summary>
public sealed class CategoryManagementServiceTests
{
    // K1: een categorie heeft een zichtbare naam en twee technische shadowwaarden; ongeldige namen schrijven niets.
    [Fact]
    public async Task Name_normalization_version_and_length_boundaries_are_enforced()
    {
        await using var db = await DatabaseAsync();
        var admin = await TrainingTestData.CreateAdministratorAsync(db.Services);
        var service = db.Services.GetRequiredService<CategoryManagementService>();
        var created = await service.CreateAsync(admin.Id, new("  E\u0301cole\t de\u00a0 soins  "));
        Assert.True(created.Succeeded);
        await using (var read = db.CreateScope())
        {
            var context = read.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await context.Categories.SingleAsync(x => x.Id == created.Id);
            Assert.Equal("École de soins", stored.Name);
            Assert.Equal("ÉCOLE DE SOINS", context.Entry(stored).Property<string>("NormalizedName").CurrentValue);
            Assert.NotEqual(Guid.Empty, context.Entry(stored).Property<Guid>("Version").CurrentValue);
        }
        Assert.True((await service.CreateAsync(admin.Id, new(new string('x', 100)))).Succeeded);
        var before = await DigestAsync(db);
        foreach (var name in new string?[] { null, "", " \t\u00a0", new('x', 101), "naam\0verboden" })
        {
            var rejected = await service.CreateAsync(admin.Id, new(name));
            Assert.Equal(TrainingOperationStatus.Invalid, rejected.Status);
            Assert.Contains("Name", rejected.Errors!.Keys);
            Assert.Equal(before, await DigestAsync(db));
        }
    }

    // K2: hoofdletters, Unicode-spaties en canonieke Unicodevorm delen dezelfde unieke opslagsleutel.
    [Theory]
    [InlineData("case")]
    [InlineData("whitespace")]
    [InlineData("formc")]
    public async Task Normalized_duplicate_is_a_field_error_for_create_and_rename(string variant)
    {
        await using var db = await DatabaseAsync();
        var admin = await TrainingTestData.CreateAdministratorAsync(db.Services);
        var first = await TrainingTestData.CreateCategoryAsync(db.Services, "École de soins");
        var other = await TrainingTestData.CreateCategoryAsync(db.Services, "Andere categorie");
        var version = await CategoryVersionAsync(db, other.Id);
        var duplicate = variant switch
        {
            "case" => "éCOLE DE SOINS",
            "whitespace" => " École\t de\u00a0 soins ",
            _ => "E\u0301cole de soins"
        };
        var before = await DigestAsync(db);
        var service = db.Services.GetRequiredService<CategoryManagementService>();
        var create = await service.CreateAsync(admin.Id, new(duplicate));
        var rename = await service.RenameAsync(admin.Id, new(other.Id, version, duplicate));
        Assert.Equal(TrainingOperationStatus.Invalid, create.Status);
        Assert.Equal(TrainingOperationStatus.Invalid, rename.Status);
        Assert.Contains("Name", create.Errors!.Keys);
        Assert.Contains("Name", rename.Errors!.Keys);
        Assert.Equal(before, await DigestAsync(db));
        Assert.NotEqual(first.Id, other.Id);
    }

    // K3: gedeelde categorieën blijven actueel voor afgelopen en geannuleerde uitvoeringen.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_preserves_training_and_historical_moments(bool cancelled)
    {
        await using var db = await DatabaseAsync();
        var admin = await TrainingTestData.CreateAdministratorAsync(db.Services);
        var owner = await TrainingTestData.CreateOwnerAsync(db.Services);
        var training = await TrainingTestData.CreateTrainingAsync(db.Services, owner, admin);
        var moment = await TrainingTestData.CreateMomentAsync(db.Services, training, date: new(2025,1,15),
            status: cancelled ? AalstAcademie.Web.Models.Domain.TrainingMomentStatus.Cancelled : AalstAcademie.Web.Models.Domain.TrainingMomentStatus.Scheduled);
        var categoryVersion = await CategoryVersionAsync(db, training.CategoryId);
        var identityBefore = await IdentityAsync(db);
        Assert.True((await db.Services.GetRequiredService<CategoryManagementService>()
            .RenameAsync(admin.Id,new(training.CategoryId,categoryVersion,"Hernoemde categorie"))).Succeeded);
        await using var read = db.CreateScope();
        var context = read.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stored = await context.Trainings.AsNoTracking().SingleAsync(x=>x.Id==training.Id);
        var storedMoment = await context.TrainingMoments.AsNoTracking().SingleAsync(x=>x.Id==moment.Id);
        Assert.Equal(training.CategoryId,stored.CategoryId);Assert.Equal(training.Version,stored.Version);
        Assert.Equal(training.InstructorUserId,stored.InstructorUserId);
        Assert.Equal(moment.Version,storedMoment.Version);Assert.Equal(moment.Date,storedMoment.Date);Assert.Equal(moment.Status,storedMoment.Status);
        Assert.Equal("Hernoemde categorie",await context.Categories.Where(x=>x.Id==stored.CategoryId).Select(x=>x.Name).SingleAsync());
        Assert.NotEqual(categoryVersion,await CategoryVersionAsync(db,stored.CategoryId));
        Assert.Equal(identityBefore,await IdentityAsync(db));
    }

    // K4: ontbrekende/lege tokens zijn geen verzoek om de nieuwste versie te gebruiken; oude tokens mogen niet overschrijven.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expected_category_version_is_required_and_current(bool stale)
    {
        await using var db = await DatabaseAsync();
        var admin = await TrainingTestData.CreateAdministratorAsync(db.Services);
        var category = await TrainingTestData.CreateCategoryAsync(db.Services);
        var old = await CategoryVersionAsync(db, category.Id);
        var service = db.Services.GetRequiredService<CategoryManagementService>();
        if (stale)
        {
            var renamed = await service.RenameAsync(admin.Id, new(category.Id, old, "Nieuwe naam"));
            Assert.Equal(TrainingOperationStatus.Success, renamed.Status);
        }
        var before = await DigestAsync(db);
        foreach (var version in stale ? new Guid?[] { old } : new Guid?[] { null, Guid.Empty })
        {
            var result = await service.RenameAsync(admin.Id, new(category.Id, version, "Verboden overschrijving"));
            Assert.Equal(TrainingOperationStatus.Conflict, result.Status);
            Assert.Equal(before, await DigestAsync(db));
        }
    }

    // K5: de injector faalt pas na de echte UPDATE; rollback moet ook de al geschreven naam/shadowversie herstellen.
    [Fact]
    public async Task Late_category_save_failure_rolls_back_and_allows_a_clean_retry()
    {
        var failure = new TrainingFailureInterceptor();
        await using var db = await DatabaseAsync(failure);
        var admin = await TrainingTestData.CreateAdministratorAsync(db.Services);
        var category = await TrainingTestData.CreateCategoryAsync(db.Services);
        var version = await CategoryVersionAsync(db, category.Id);
        var before = await DigestAsync(db);
        var service = db.Services.GetRequiredService<CategoryManagementService>();
        failure.Arm("category-save");
        var rejected = await service.RenameAsync(admin.Id, new(category.Id, version, "Moet terugrollen"));
        Assert.Equal(TrainingOperationStatus.StorageUnavailable, rejected.Status);
        Assert.True(failure.Triggered);
        Assert.True(failure.SawParentWrite);
        Assert.Equal(before, await DigestAsync(db));
        failure.Disarm();
        var retry = await service.RenameAsync(admin.Id, new(category.Id, version, "Schone herhaling"));
        Assert.Equal(TrainingOperationStatus.Success, retry.Status);
        Assert.NotEqual(version, await CategoryVersionAsync(db, category.Id));
    }

    // K6: geldige namen/versies voorkomen dat een toevallige validatiefout wordt aangezien voor echte autorisatie.
    [Theory]
    [InlineData("missing")]
    [InlineData("ineligible")]
    [InlineData("revoked")]
    [InlineData("employee")]
    [InlineData("instructor")]
    public async Task Nonadministrator_cannot_create_or_rename_category(string group)
    {
        var variants = group switch
        {
            "ineligible" => new[] { "pending", "refused", "blocked", "demo" },
            "instructor" => new[] { "internal", "external" },
            _ => new[] { group }
        };
        foreach (var variant in variants)
        {
            await using var db = await DatabaseAsync(demoEnabled: variant != "demo");
            var category = await TrainingTestData.CreateCategoryAsync(db.Services);
            var version = await CategoryVersionAsync(db, category.Id);
            var actorId = "ontbrekende-actor";
            if (variant != "missing")
            {
                var actor = variant switch
                {
                    "employee" => await TestAccountData.CreateAsync(db.Services, RequestedAccountType.Employee,
                        AccountApprovalStatus.Approved, roles: [RoleNames.Medewerker]),
                    "internal" => await TrainingTestData.CreateOwnerAsync(db.Services),
                    "external" => await TrainingTestData.CreateOwnerAsync(db.Services, external: true),
                    _ => await TrainingTestData.CreateAdministratorAsync(db.Services)
                };
                actorId = actor.Id;
                db.Context.ChangeTracker.Clear();
                var current = await db.Context.Users.SingleAsync(x => x.Id == actor.Id);
                if (variant == "pending") current.AccountApprovalStatus = AccountApprovalStatus.Pending;
                if (variant == "refused") current.AccountApprovalStatus = AccountApprovalStatus.Refused;
                if (variant == "blocked") current.IsBlocked = true;
                if (variant == "demo") current.DemoSeedKey = "sprint003.test.administrator";
                await db.Context.SaveChangesAsync();
                if (variant == "revoked")
                    Assert.True((await db.Services.GetRequiredService<UserManager<ApplicationUser>>()
                        .RemoveFromRoleAsync(current, RoleNames.Beheerder)).Succeeded);
            }
            var before = await DigestAsync(db);
            var service = db.Services.GetRequiredService<CategoryManagementService>();
            Assert.Equal(TrainingOperationStatus.Forbidden, (await service.CreateAsync(actorId, new("Geldige nieuwe naam"))).Status);
            Assert.Equal(TrainingOperationStatus.Forbidden, (await service.RenameAsync(actorId, new(category.Id, version, "Geldige wijziging"))).Status);
            Assert.Equal(before, await DigestAsync(db));
        }
    }

    private static async Task<FileSqliteTestDatabase> DatabaseAsync(TrainingFailureInterceptor? failure = null, bool demoEnabled = true)
    {
        var db = await FileSqliteTestDatabase.CreateAsync(interceptor: failure,
            configureServices: services => TrainingTestServices.Configure(services, demoEnabled));
        await TrainingTestData.InitializeAsync(db.Services);
        return db;
    }

    // Elke bewijslezing gebruikt een andere scope/verbinding dan de mutatieservice.
    private static async Task<string> DigestAsync(FileSqliteTestDatabase db)
    {
        await using var read = db.CreateScope();
        return await TrainingTestData.DigestAsync(read.ServiceProvider);
    }

    private static async Task<Guid> CategoryVersionAsync(FileSqliteTestDatabase db, int id)
    {
        await using var read = db.CreateScope();
        return await read.ServiceProvider.GetRequiredService<ApplicationDbContext>().Categories.AsNoTracking()
            .Where(x => x.Id == id).Select(x => EF.Property<Guid>(x, "Version")).SingleAsync();
    }

    private static async Task<string[]> IdentityAsync(FileSqliteTestDatabase db)
    {
        await using var read = db.CreateScope();
        var context = read.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = await context.Users.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id + "|" + x.SecurityStamp + "|" +
            x.ConcurrencyStamp + "|" + x.AccountApprovalStatus + "|" + x.IsBlocked + "|" + x.RequestedAccountType).ToArrayAsync();
        var roles = await context.UserRoles.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.RoleId)
            .Select(x => x.UserId + "|" + x.RoleId).ToArrayAsync();
        var internalProfiles = await context.InternalInstructors.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => x.Id + "|" + x.ApplicationUserId).ToArrayAsync();
        var externalProfiles = await context.ExternalInstructors.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => x.Id + "|" + x.ApplicationUserId + "|" + x.OrganizationName).ToArrayAsync();
        return [.. users, .. roles, .. internalProfiles, .. externalProfiles];
    }
}
