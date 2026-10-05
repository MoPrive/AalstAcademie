using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Enrolment;

/// <summary>Een goedgekeurde gebruiker annuleert uitsluitend zijn eigen poging; geen retourbuffer, wel dezelfde plaats vullen.</summary>
public sealed class RegistrationCancellationService(ApplicationDbContext context, EmployeeAccessReader access,
    TrainingWriteTransaction transaction, TrainingSchedule schedule, WaitlistPromotionService promotion)
{
    public Task<TrainingOperationResult> CancelAsync(string? actorId, int id, RegistrationCancelInput input, CancellationToken ct = default) =>
        transaction.ExecuteAsync(async token =>
        {
            if (await access.ReadApprovedAsync(actorId, token) is null) return new(TrainingOperationStatus.Forbidden);
            var row = await context.Registrations.AsNoTracking().Include(x => x.TrainingMoment)
                .SingleOrDefaultAsync(x => x.Id == id && x.ApplicationUserId == actorId, token);
            if (row is null) return new(TrainingOperationStatus.NotFound);
            var errors = RegistrationWorkflowValidation.Versions(input);
            if (errors.Count != 0) return new(TrainingOperationStatus.Invalid, Errors: errors);
            var moment = row.TrainingMoment;
            if (row.Version != input.ExpectedRegistrationVersion || moment.Version != input.ExpectedMomentVersion ||
                row.Status is not (RegistrationStatus.Requested or RegistrationStatus.Confirmed) || !schedule.CanEdit(moment))
                return new(TrainingOperationStatus.Conflict);
            var count = await context.Registrations.Where(x => x.Id == id && x.ApplicationUserId == actorId &&
                x.Version == input.ExpectedRegistrationVersion && x.Status == row.Status).ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.Status, RegistrationStatus.Cancelled).SetProperty(x => x.Version, Guid.NewGuid()), token);
            if (count != 1) return new(TrainingOperationStatus.Conflict);
            var fill = await promotion.FillMomentInsideTransactionAsync(moment.Id, token);
            if (!fill.Succeeded) return fill;
            return new(schedule.CanEdit(moment) ? TrainingOperationStatus.Success : TrainingOperationStatus.Conflict, id);
        }, ct);
}
