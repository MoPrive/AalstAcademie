// Sprint 003: Projecteert geautoriseerde huidige definities met filters, titel/ID-sortering, paginaomvang twintig en historieflags
// Reads veranderen geen opslag of versies.
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Models.Training;
using Microsoft.EntityFrameworkCore;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Web.Services.Training;

/// <summary>Geautoriseerde definitiereads projecteren huidige namen en echte historie, zonder opslagmutatie.</summary>
public sealed class TrainingManagementQueries(ApplicationDbContext context, TrainingAccessReader access,
    TrainingFoundationQueries foundation, TrainingMomentManagementQueries moments)
{
    private const int PageSize = 20;

    /// <summary>Alleen categorie/doelgroep/pagina; titel en sleutel maken de pagina's stabiel zonder planningfilter.</summary>
    public async Task<TrainingQueryResult<TrainingPageReadModel>> GetListAsync(string actorUserId, TrainingListFilter filter,
        CancellationToken cancellationToken = default)
    {
        var actor = await access.ReadActorAsync(actorUserId, cancellationToken);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(filter.Audience)) errors["Audience"] = ["Kies een geldig doelgroepfilter."];
        if (filter.Page <= 0) errors["Page"] = ["Kies een positief paginanummer."];
        if (filter.CategoryId is <= 0) errors["CategoryId"] = ["Kies een bestaande categorie."];
        if (filter.CategoryId is int categoryId && categoryId > 0 &&
            !await context.Categories.AsNoTracking().AnyAsync(x => x.Id == categoryId, cancellationToken))
            errors["CategoryId"] = ["Kies een bestaande categorie."];
        if (errors.Count != 0) return new(TrainingOperationStatus.Invalid, Errors: errors);
        var query = InScope(actor);
        query = filter.Audience switch
        {
            TrainingAudienceFilter.Unassigned => query.Where(x => x.AudienceScope == TrainingAudienceScope.Unassigned),
            TrainingAudienceFilter.AllDepartments => query.Where(x => x.AudienceScope == TrainingAudienceScope.AllDepartments),
            TrainingAudienceFilter.SelectedDepartments => query.Where(x => x.AudienceScope == TrainingAudienceScope.SelectedDepartments),
            _ => query
        };
        if (filter.CategoryId is int selectedCategoryId) query = query.Where(x => x.CategoryId == selectedCategoryId);
        var count = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(count / (double)PageSize));
        var rows = new List<TrainingRowReadModel>();
        if (filter.Page <= totalPages)
        {
            var trainings = await query.Include(x => x.Category).Include(x => x.Instructor)
                .OrderBy(x => x.Title).ThenBy(x => x.Id).Skip((filter.Page - 1) * PageSize).Take(PageSize).ToListAsync(cancellationToken);
            foreach (var training in trainings) rows.Add(await ToRowAsync(training, cancellationToken));
        }
        return new(TrainingOperationStatus.Success, new(rows, filter.Page, totalPages, count,
            await context.Categories.AsNoTracking().AnyAsync(cancellationToken)));
    }

    /// <summary>Foreign/missing zijn gelijk NotFound; UI-flags volgen actuele rights en historie, geen globale startgrens.</summary>
    public async Task<TrainingQueryResult<TrainingDetailsReadModel>> GetDetailsAsync(string actorUserId, int id,
        CancellationToken cancellationToken = default)
    {
        var actor = await access.ReadActorAsync(actorUserId, cancellationToken);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var training = await InScope(actor).Include(x => x.Category).Include(x => x.Instructor)
            .Include(x => x.TrainingDepartments).ThenInclude(x => x.Department).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (training is null) return new(TrainingOperationStatus.NotFound);
        var history = await foundation.HasTrainingHistoryAsync(id, cancellationToken);
        var momentList = await moments.GetListAsync(actorUserId, id, cancellationToken);
        if (!momentList.Succeeded) return new(momentList.Status, Errors: momentList.Errors);
        var departments = training.TrainingDepartments.OrderBy(x => x.Department.Name).ThenBy(x => x.DepartmentId)
            .Select(x => new TrainingDepartmentReadModel(x.DepartmentId, x.Department.Name)).ToArray();
        return new(TrainingOperationStatus.Success, new(await ToRowAsync(training, cancellationToken), training.Description,
            departments, true, actor.IsAdministrator, !history, history, momentList.Value ?? [],
            await access.ReadEligibleOwnerAsync(training.InstructorUserId, cancellationToken) is not null));
    }

    /// <summary>Alleen huidige beheerders krijgen de selector met nu geschikte lesgevers.</summary>
    public async Task<TrainingQueryResult<IReadOnlyList<TrainingOwnerReadModel>>> GetEligibleOwnersAsync(string actorUserId,
        CancellationToken cancellationToken = default)
    {
        if ((await access.ReadActorAsync(actorUserId, cancellationToken))?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
        var ids = await (from user in context.Users.AsNoTracking()
            join grant in context.UserRoles.AsNoTracking() on user.Id equals grant.UserId
            join role in context.Roles.AsNoTracking() on grant.RoleId equals role.Id
            where role.NormalizedName == RoleNames.Lesgever.ToUpperInvariant()
            select user.Id).Distinct().ToListAsync(cancellationToken);
        var owners = new List<TrainingOwnerReadModel>();
        foreach (var id in ids)
            if (await access.ReadEligibleOwnerAsync(id, cancellationToken) is { } owner) owners.Add(owner);
        return new(TrainingOperationStatus.Success, owners.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.UserId, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Referentiekeuze verleent geen categorie- of afdelingsbeheer.</summary>
    public async Task<TrainingQueryResult<TrainingReferencesReadModel>> GetReferencesAsync(string actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (await access.ReadActorAsync(actorUserId, cancellationToken) is null) return new(TrainingOperationStatus.Forbidden);
        return new(TrainingOperationStatus.Success, new(await CategoryRows().ToListAsync(cancellationToken),
            await context.Departments.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id)
                .Select(x => new TrainingDepartmentReadModel(x.Id, x.Name)).ToListAsync(cancellationToken)));
    }

    /// <summary>Categorielijst voor beheer blijft admin-only en bevat de bestaande shadowversie.</summary>
    public async Task<TrainingQueryResult<IReadOnlyList<TrainingCategoryReadModel>>> GetCategoriesAsync(string actorUserId,
        CancellationToken cancellationToken = default)
    {
        if ((await access.ReadActorAsync(actorUserId, cancellationToken))?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
        return new(TrainingOperationStatus.Success, await CategoryRows().ToListAsync(cancellationToken));
    }

    public async Task<TrainingQueryResult<TrainingCategoryReadModel>> GetCategoryAsync(string actorUserId, int id,
        CancellationToken cancellationToken = default)
    {
        if ((await access.ReadActorAsync(actorUserId, cancellationToken))?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
        var category = await context.Categories.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new TrainingCategoryReadModel(x.Id, x.Name, EF.Property<Guid>(x, "Version")))
            .SingleOrDefaultAsync(cancellationToken);
        return category is null ? new(TrainingOperationStatus.NotFound) : new(TrainingOperationStatus.Success, category);
    }

    // De eigenaarbeperking zit in de databasequery vóór filters en paginering, niet uitsluitend in de view.
    private IQueryable<TrainingEntity> InScope(TrainingActor actor) => actor.IsAdministrator
        ? context.Trainings.AsNoTracking() : context.Trainings.AsNoTracking().Where(x => x.InstructorUserId == actor.UserId);
    // Shadowproperties worden alleen in het leescontract geprojecteerd; het formulier krijgt geen complete tracked entiteit.
    private IQueryable<TrainingCategoryReadModel> CategoryRows() => context.Categories.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id)
        .Select(x => new TrainingCategoryReadModel(x.Id, x.Name, EF.Property<Guid>(x, "Version")));
    // Projecteer huidige gedeelde namen en profielvorm. Een readmodel is geen historische snapshot of opdracht om opslag te repareren.
    private async Task<TrainingRowReadModel> ToRowAsync(TrainingEntity training, CancellationToken token)
    {
        var shape = await access.ReadOwnerShapeAsync(training.InstructorUserId, token);
        // Onvolledige legacygegevens krijgen leesbare feedback; de writeguard blijft afzonderlijk streng.
        return new(training.Id, training.Title, training.CategoryId, training.Category.Name, training.InstructorUserId,
            shape?.DisplayName ?? "Onvolledig lesgeverprofiel",
            shape?.IsExternal ?? training.Instructor.RequestedAccountType == RequestedAccountType.ExternalInstructor,
            training.ExternalTotalPriceEuros, training.AudienceScope, training.RequiresMotivation, training.Version);
    }
}
