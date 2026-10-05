using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Enrolment;

/// <summary>Actueel toekomstaanbod voor de eigen afdeling. De DTO bevat geen deelnemers of hun contactgegevens.</summary>
public sealed class EmployeeCatalogueQueries(ApplicationDbContext context, EmployeeAccessReader access, ParticipationEligibility eligibility)
{
    public const int PageSize = 20;
    public async Task<EmployeeCataloguePage> GetAsync(string? actorId, int? categoryId = null, int page = 1, CancellationToken ct = default)
    {
        await using var read = context.Database.CurrentTransaction is null ? await context.Database.BeginTransactionAsync(ct) : null;
        var actor = await access.ReadEmployeeAsync(actorId, ct);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var trainingIds = await context.Trainings.AsNoTracking().Where(x => x.AudienceScope == TrainingAudienceScope.AllDepartments ||
            x.AudienceScope == TrainingAudienceScope.SelectedDepartments && x.TrainingDepartments.Any(d => d.DepartmentId == actor.DepartmentId))
            .Select(x => x.Id).ToArrayAsync(ct);
        var state = await eligibility.ReadAsync(trainingIds, ct);
        var all = state.Moments.Where(eligibility.IsNewMoment).OrderBy(x => x.Date).ThenBy(x => x.StartTime).ThenBy(x => x.Id).ToArray();
        var categories = all.Select(x => state.Trainings[x.TrainingId].Category).DistinctBy(x => x.Id)
            .OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => new EmployeeCategoryChoice(x.Id, x.Name)).ToArray();
        var filtered = all.Where(x => categoryId is null || state.Trainings[x.TrainingId].CategoryId == categoryId).ToArray();
        var pages = Math.Max(1, (filtered.Length + PageSize - 1) / PageSize); page = Math.Clamp(page, 1, pages);
        var organizations = await OrganizationsAsync(trainingIds, ct);
        var rows = filtered.Skip((page - 1) * PageSize).Take(PageSize).Select(x => Row(x, state, organizations)).ToArray();
        return new(TrainingOperationStatus.Success, page, pages, filtered.Length, categoryId, rows, categories);
    }

    public async Task<EmployeeMomentReadResult> GetMomentAsync(string? actorId, int momentId, CancellationToken ct = default)
    {
        await using var read = context.Database.CurrentTransaction is null ? await context.Database.BeginTransactionAsync(ct) : null;
        var actor = await access.ReadEmployeeAsync(actorId, ct);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var moment = await context.TrainingMoments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == momentId, ct);
        if (moment is null) return new(TrainingOperationStatus.NotFound);
        var state = await eligibility.ReadAsync([moment.TrainingId], ct);
        var training = state.Trainings[moment.TrainingId];
        if (!eligibility.Allows(actor.DepartmentId!.Value, training)) return new(TrainingOperationStatus.Forbidden);
        if (!eligibility.IsNewMoment(moment)) return new(TrainingOperationStatus.Conflict);
        // De geladen bulkrij bevat ook de actuele zaal; een losse eerder getrackte navigatie wordt niet gebruikt.
        moment = state.Moments.Single(x => x.Id == momentId);
        return new(TrainingOperationStatus.Success, new(Row(moment, state, await OrganizationsAsync([training.Id], ct)),
            training.Description, training.RequiresMotivation, training.Version, moment.Version, actor,
            eligibility.HasActiveParticipation(actor.UserId, training.Id, state), eligibility.WasRefused(actor.UserId, momentId, state)));
    }

    private Task<Dictionary<string, string>> OrganizationsAsync(int[] trainingIds, CancellationToken ct) => context.ExternalInstructors.AsNoTracking()
        .Where(x => context.Trainings.Any(t => trainingIds.Contains(t.Id) && t.InstructorUserId == x.ApplicationUserId))
        .ToDictionaryAsync(x => x.ApplicationUserId, x => x.OrganizationName, ct);
    private EmployeeCatalogueRow Row(TrainingMoment moment, ParticipationSnapshot state, IReadOnlyDictionary<string, string> organizations)
    {
        var t = state.Trainings[moment.TrainingId]; var a = eligibility.Availability(moment, state);
        var name = organizations.TryGetValue(t.InstructorUserId, out var organization) ? organization : $"{t.Instructor.FirstName} {t.Instructor.LastName}".Trim();
        return new(moment.Id, t.Id, t.Title, t.Category.Name, moment.Date, moment.StartTime, moment.EndTime,
            moment.Location.Name, moment.Location.Address, name, t.ExternalTotalPriceEuros, a.PhysicalAvailable, a.EligibleWaiters, a.DirectAvailable);
    }
}
