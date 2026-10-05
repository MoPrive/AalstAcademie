using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Accounts;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Wijzigt uitsluitend de nullable head-FK onder dezelfde vroege SQLite-write-lock als de managerkeuze.</summary>
public sealed class DepartmentResponsibleService(ApplicationDbContext context, TrainingAccessReader access,
    DepartmentResponsibleQueries queries, TrainingWriteTransaction transaction)
{
    public Task<TrainingOperationResult> AssignAsync(string? actorId, int departmentId, DepartmentResponsibleInput input,
        CancellationToken ct = default) => transaction.ExecuteAsync(async token =>
    {
        if ((await access.ReadActorAsync(actorId, token))?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
        var department = await context.Departments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == departmentId, token);
        if (department is null) return new(TrainingOperationStatus.NotFound);
        var candidateId = string.IsNullOrWhiteSpace(input.ResponsibleUserId) ? null : input.ResponsibleUserId.Trim();
        if (candidateId is not null && !await queries.Candidates.AnyAsync(x => x.Id == candidateId, token))
            return new(TrainingOperationStatus.Invalid, Errors: new Dictionary<string, string[]> { ["ResponsibleUserId"] = ["Kies een geschikt goedgekeurd persoonlijk account."] });
        if (department.ResponsibleUserId != input.ExpectedResponsibleUserId) return new(TrainingOperationStatus.Conflict);
        // Expliciete nullable-CAS beschermt ook twee beheerders die beiden oorspronkelijk Geen zagen.
        var count = await context.Departments.Where(x => x.Id == departmentId && x.ResponsibleUserId == input.ExpectedResponsibleUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, candidateId), token);
        return new(count == 1 ? TrainingOperationStatus.Success : TrainingOperationStatus.Conflict, departmentId);
    }, ct);
}
