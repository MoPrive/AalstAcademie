// Sprint 003: Controleert actuele adminrechten, normaliseert categorieën en schrijft create/rename met uniciteit en CAS in één transactie
// De categorie-ID en bestaande opleidingslinks blijven gelijk.
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Training;

/// <summary>Categorieaanmaak en rename zijn zelfstandige adminmutaties, zonder Training- of Identitywijzigingen.</summary>
public sealed class CategoryManagementService(ApplicationDbContext context, TrainingAccessReader access,
    TrainingWriteTransaction transactions)
{
    /// <summary>Bewaart genormaliseerde naam, unieke sleutel en eerste shadowversie in dezelfde transactie.</summary>
    public Task<TrainingOperationResult> CreateAsync(string actorUserId, CategoryCreateCommand command,
        CancellationToken cancellationToken = default) => transactions.ExecuteAsync(async token =>
    {
        if ((await access.ReadActorAsync(actorUserId, token))?.IsAdministrator != true)
            return new(TrainingOperationStatus.Forbidden);
        var name = TrainingValueValidation.ValidateCategoryName(command.Name);
        if (!name.Succeeded) return new(TrainingOperationStatus.Invalid, Errors: name.Errors);
        if (await NameExistsAsync(name.NormalizedName!, null, token)) return DuplicateName();
        var category = new Category { Name = name.Name! };
        context.Categories.Add(category);
        context.Entry(category).Property<string>("NormalizedName").CurrentValue = name.NormalizedName!;
        context.Entry(category).Property<Guid>("Version").CurrentValue = Guid.NewGuid();
        await context.SaveChangesAsync(token);
        return new(TrainingOperationStatus.Success, category.Id);
    }, cancellationToken, categoryNameUniqueness: true);

    /// <summary>Vergelijkt de bewust gelezen shadowversie voordat naam/sleutel/nieuwe versie samen wijzigen.</summary>
    public Task<TrainingOperationResult> RenameAsync(string actorUserId, CategoryRenameCommand command,
        CancellationToken cancellationToken = default) => transactions.ExecuteAsync(async token =>
    {
        // Ook een directe servicecall heeft actuele beheerrechten nodig; de controller is geen trusted bypass.
        if ((await access.ReadActorAsync(actorUserId, token))?.IsAdministrator != true)
            return new(TrainingOperationStatus.Forbidden);
        // Een detached Category draagt haar shadowwaarden niet mee. Verwijder alleen de eerdere
        // lokale kopie en laad binnen de write-lock een complete verse snapshot met beide shadowvelden.
        var previouslyTracked = context.Categories.Local.FirstOrDefault(x => x.Id == command.Id);
        if (previouslyTracked is not null) context.Entry(previouslyTracked).State = EntityState.Detached;
        var stored = await context.Categories.SingleOrDefaultAsync(x => x.Id == command.Id, token);
        if (stored is null) return new(TrainingOperationStatus.NotFound);
        var version = context.Entry(stored).Property<Guid>("Version");
        if (command.ExpectedVersion is null || command.ExpectedVersion == Guid.Empty || command.ExpectedVersion != version.CurrentValue)
            return new(TrainingOperationStatus.Conflict);
        var name = TrainingValueValidation.ValidateCategoryName(command.Name);
        if (!name.Succeeded) return new(TrainingOperationStatus.Invalid, Errors: name.Errors);
        if (await NameExistsAsync(name.NormalizedName!, stored.Id, token)) return DuplicateName();

        version.OriginalValue = command.ExpectedVersion.Value;
        version.CurrentValue = Guid.NewGuid();
        stored.Name = name.Name!;
        context.Entry(stored).Property<string>("NormalizedName").CurrentValue = name.NormalizedName!;
        await context.SaveChangesAsync(token);
        // CategoryId en bestaande Training-FKs/Training.Version worden hierbij nooit aangeraakt.
        return new(TrainingOperationStatus.Success, stored.Id);
    }, cancellationToken, categoryNameUniqueness: true);

    // Vergelijk de opgeslagen genormaliseerde shadowwaarde; bij rename wordt alleen de eigen categorie uitgesloten.
    private Task<bool> NameExistsAsync(string normalizedName, int? excludedId, CancellationToken cancellationToken) =>
        context.Categories.AsNoTracking().AnyAsync(x => EF.Property<string>(x, "NormalizedName") == normalizedName &&
            (excludedId == null || x.Id != excludedId), cancellationToken);

    private static TrainingOperationResult DuplicateName() => new(TrainingOperationStatus.Invalid, Errors:
        new Dictionary<string, string[]> { ["Name"] = ["Deze categorienaam bestaat al."] });
}
