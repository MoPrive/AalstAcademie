// Sprint 003: Schrijft eigen of administratieve definities en admin-doelgroepen onder de vroege SQLite-write-lock
// Historie vergrendelt werkelijk gewijzigde prijs/motivatie, maar niet gewone metadata of doelgroep.
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Models.Domain;
using Microsoft.EntityFrameworkCore;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Web.Services.Training;

/// <summary>Definitie- en doelgroepbeheer leest alle guards ná de vroege gezamenlijke SQLite-write-lock.</summary>
public sealed class TrainingManagementService(ApplicationDbContext context, TrainingAccessReader access,
    TrainingWriteTransaction transactions, TrainingValueValidation validation, TrainingFoundationQueries foundation, WaitlistPromotionService promotion)
{
    /// <summary>Eigen actor of gekozen huidige lesgever; de nieuwe definitie begint altijd Unassigned.</summary>
    public Task<TrainingOperationResult> CreateAsync(string actorUserId, TrainingCreateCommand command,
        CancellationToken cancellationToken = default) => transactions.ExecuteAsync(async token =>
    {
        var actor = await access.ReadActorAsync(actorUserId, token);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        if (!actor.IsAdministrator && command.OwnerUserId is not null)
            return Invalid("OwnerUserId", "Een lesgever kiest geen andere eigenaar.");
        var owner = await access.ReadEligibleOwnerAsync(actor.IsAdministrator ? command.OwnerUserId : actor.UserId, token);
        if (owner is null) return Invalid("OwnerUserId", "Kies een actuele goedgekeurde lesgever met een passend profiel.");
        var fields = validation.ValidateFields(command.Fields, owner.IsExternal);
        if (!fields.Succeeded) return new(TrainingOperationStatus.Invalid, Errors: fields.Errors);
        var referenceError = await ValidateReferencesAsync(fields.Fields!.CategoryId, [], token);
        if (referenceError is not null) return referenceError;
        var training = new TrainingEntity { InstructorUserId = owner.UserId,
            AudienceScope = TrainingAudienceScope.Unassigned, Version = Guid.NewGuid() };
        ApplyFields(training, fields.Fields);
        context.Trainings.Add(training);
        await context.SaveChangesAsync(token);
        return new(TrainingOperationStatus.Success, training.Id);
    }, cancellationToken);

    /// <summary>Metadata blijft historisch bewerkbaar; daadwerkelijk gewijzigde voorwaarden eisen nog geen historie.</summary>
    public Task<TrainingOperationResult> UpdateAsync(string actorUserId, TrainingUpdateCommand command,
        CancellationToken cancellationToken = default) => transactions.ExecuteAsync(async token =>
    {
        var actor = await access.ReadActorAsync(actorUserId, token);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var training = await ReadInScopeAsync(actor, command.Id, token);
        if (training is null) return new(TrainingOperationStatus.NotFound);
        if (!VersionMatches(training, command.ExpectedVersion)) return new(TrainingOperationStatus.Conflict);
        var owner = await access.ReadOwnerShapeAsync(training.InstructorUserId, token);
        if (owner is null) return Invalid("OwnerUserId", "Het bestaande eigenaarprofiel past niet bij deze opleiding.");
        // Ontbrekende checkbox/prijs wist niets; uitsluitend de vertrouwde commandopbouw bepaalt presence.
        var effective = command.Fields with
        {
            ExternalTotalPriceEuros = command.PriceSupplied ? command.Fields.ExternalTotalPriceEuros : training.ExternalTotalPriceEuros,
            RequiresMotivation = command.MotivationSupplied ? command.Fields.RequiresMotivation : training.RequiresMotivation
        };
        var fields = validation.ValidateFields(effective, owner.IsExternal);
        if (!fields.Succeeded) return new(TrainingOperationStatus.Invalid, Errors: fields.Errors);
        if ((fields.Fields!.ExternalTotalPriceEuros != training.ExternalTotalPriceEuros ||
             fields.Fields.RequiresMotivation != training.RequiresMotivation) && await foundation.HasTrainingHistoryAsync(training.Id, token))
            return new(TrainingOperationStatus.Conflict);
        var referenceError = await ValidateReferencesAsync(fields.Fields.CategoryId, [], token);
        if (referenceError is not null) return referenceError;
        AttachCurrent(training, command.ExpectedVersion!.Value);
        ApplyFields(training, fields.Fields);
        training.Version = Guid.NewGuid();
        await context.SaveChangesAsync(token);
        return new(TrainingOperationStatus.Success, training.Id);
    }, cancellationToken);

    /// <summary>Doelgroep mag na momentstart/historie wijzigen zonder status-, buffer- of voorwaardenmutatie.</summary>
    public Task<TrainingOperationResult> AssignAudienceAsync(string actorUserId, TrainingAudienceCommand command,
        CancellationToken cancellationToken = default) => transactions.ExecuteAsync(async token =>
    {
        var actor = await access.ReadActorAsync(actorUserId, token);
        if (actor?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
        var training = await ReadInScopeAsync(actor, command.Id, token);
        if (training is null) return new(TrainingOperationStatus.NotFound);
        if (!VersionMatches(training, command.ExpectedVersion)) return new(TrainingOperationStatus.Conflict);
        var audience = TrainingValueValidation.ValidateAudience(command.Scope, command.DepartmentIds, allowUnassigned: false);
        if (!audience.Succeeded) return new(TrainingOperationStatus.Invalid, Errors: audience.Errors);
        var referenceError = await ValidateReferencesAsync(training.CategoryId, audience.DepartmentIds, token);
        if (referenceError is not null) return referenceError;
        var oldLinks = await context.TrainingDepartments.AsNoTracking().Where(x => x.TrainingId == training.Id).ToListAsync(token);
        AttachCurrent(training, command.ExpectedVersion!.Value);
        training.AudienceScope = audience.Scope;
        training.Version = Guid.NewGuid();
        // Parent- en eventuele childwrites blijven in één transactie, ook bij een late childfout.
        await context.SaveChangesAsync(token);
        var wanted = audience.DepartmentIds.ToHashSet();
        foreach (var link in oldLinks.Where(x => !wanted.Contains(x.DepartmentId))) context.TrainingDepartments.Remove(link);
        var existing = oldLinks.Select(x => x.DepartmentId).ToHashSet();
        foreach (var id in audience.DepartmentIds.Where(x => !existing.Contains(x)))
            context.TrainingDepartments.Add(new() { TrainingId = training.Id, DepartmentId = id });
        await context.SaveChangesAsync(token);
        var fill = await promotion.FillTrainingInsideTransactionAsync(training.Id, token);
        return fill.Succeeded ? new(TrainingOperationStatus.Success, training.Id) : fill;
    }, cancellationToken);

    // Een bestaand ID buiten eigenaarsscope wordt als niet gevonden behandeld; het ID alleen verleent geen toegang.
    private async Task<TrainingEntity?> ReadInScopeAsync(TrainingActor actor, int id, CancellationToken token)
    {
        var training = await context.Trainings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return training is not null && await access.CanReadTrainingAsync(actor, training, token) ? training : null;
    }

    // De write-lock is al verkregen: categorie en geselecteerde afdelingen worden uit de huidige opslag gecontroleerd.
    private async Task<TrainingOperationResult?> ValidateReferencesAsync(int categoryId, IReadOnlyList<int> ids, CancellationToken token)
    {
        if (!await context.Categories.AsNoTracking().AnyAsync(x => x.Id == categoryId, token))
            return Invalid("CategoryId", "Kies een bestaande categorie.");
        if (ids.Count != 0 && await context.Departments.AsNoTracking().CountAsync(x => ids.Contains(x.Id), token) != ids.Count)
            return Invalid("DepartmentIds", "Kies uitsluitend bestaande afdelingen.");
        return null;
    }

    // Vervang eventuele oude tracking door de zojuist gelezen rij; EF gebruikt de geposte versie als oorspronkelijke SQL-voorwaarde.
    private void AttachCurrent(TrainingEntity training, Guid expected)
    {
        var old = context.Trainings.Local.FirstOrDefault(x => x.Id == training.Id);
        if (old is not null) context.Entry(old).State = EntityState.Detached;
        foreach (var link in context.ChangeTracker.Entries<TrainingDepartment>().Where(x => x.Entity.TrainingId == training.Id).ToArray())
            link.State = EntityState.Detached;
        context.Trainings.Attach(training);
        // Het SQL-WHERE gebruikt de oorspronkelijke gelezen versie, niet de nieuw gegenereerde token.
        context.Entry(training).Property(x => x.Version).OriginalValue = expected;
    }

    private static bool VersionMatches(TrainingEntity training, Guid? expected) =>
        expected is not null && expected != Guid.Empty && expected == training.Version;
    // Alleen gevalideerde metadata en voorwaarden worden gekopieerd; eigenaar, doelgroep en planning blijven buiten deze helper.
    private static void ApplyFields(TrainingEntity training, ValidatedTrainingFields fields)
    {
        training.Title = fields.Title; training.Description = fields.Description; training.CategoryId = fields.CategoryId;
        training.ExternalTotalPriceEuros = fields.ExternalTotalPriceEuros; training.RequiresMotivation = fields.RequiresMotivation;
    }
    private static TrainingOperationResult Invalid(string field, string message) =>
        new(TrainingOperationStatus.Invalid, Errors: new Dictionary<string, string[]> { [field] = [message] });
}
