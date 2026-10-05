using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Enrolment;

/// <summary>Actuele eigenaar/admin beoordeelt één poging met registration- én moment-CAS; weigering vult dezelfde plaats atomair.</summary>
public sealed class RegistrationReviewService(ApplicationDbContext context, TrainingAccessReader access,
    TrainingWriteTransaction transaction, TrainingSchedule schedule, WaitlistPromotionService promotion)
{
    public Task<TrainingOperationResult> ConfirmAsync(string? actorId, int id, RegistrationConfirmInput input, CancellationToken ct = default) =>
        ReviewAsync(actorId, id, input, null, false, ct);
    public Task<TrainingOperationResult> RefuseAsync(string? actorId, int id, RegistrationRefuseInput input, CancellationToken ct = default) =>
        ReviewAsync(actorId, id, input, input.DecisionReason, true, ct);

    private Task<TrainingOperationResult> ReviewAsync(string? actorId, int id, RegistrationConfirmInput input,
        string? rawReason, bool refuse, CancellationToken ct) => transaction.ExecuteAsync(async token =>
    {
        var actor = await access.ReadActorAsync(actorId, token);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var row = await context.Registrations.AsNoTracking().Include(x => x.TrainingMoment).ThenInclude(x => x.Training)
            .SingleOrDefaultAsync(x => x.Id == id, token);
        if (row is null || !await access.CanReadTrainingAsync(actor, row.TrainingMoment.Training, token)) return new(TrainingOperationStatus.NotFound);
        if (refuse && row.Status == RegistrationStatus.Confirmed && !actor.IsAdministrator) return new(TrainingOperationStatus.Forbidden);
        var errors = RegistrationWorkflowValidation.Versions(input);
        var reason = RegistrationWorkflowValidation.Reason(rawReason);
        if (refuse && !RegistrationWorkflowValidation.ValidReason(reason)) errors["DecisionReason"] = ["Gebruik maximaal 1000 tekens zonder nultekens."];
        if (errors.Count != 0) return new(TrainingOperationStatus.Invalid, Errors: errors);
        var moment = row.TrainingMoment;
        if (row.Version != input.ExpectedRegistrationVersion || moment.Version != input.ExpectedMomentVersion || !schedule.CanEdit(moment) ||
            (row.Status != RegistrationStatus.Requested && !(refuse && actor.IsAdministrator && row.Status == RegistrationStatus.Confirmed)))
            return new(TrainingOperationStatus.Conflict);
        if (!refuse && await context.Registrations.CountAsync(x => x.TrainingMomentId == moment.Id &&
            (x.Status == RegistrationStatus.Requested || x.Status == RegistrationStatus.Confirmed), token) > moment.MaximumParticipants)
            return new(TrainingOperationStatus.Conflict);
        if (!schedule.CanEdit(moment)) return new(TrainingOperationStatus.Conflict);
        var rows = context.Registrations.Where(x => x.Id == id && x.Version == input.ExpectedRegistrationVersion && x.Status == row.Status);
        var count = refuse
            ? await rows.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RegistrationStatus.Refused)
                .SetProperty(x => x.DecisionReason, reason).SetProperty(x => x.Version, Guid.NewGuid()), token)
            : await rows.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RegistrationStatus.Confirmed)
                .SetProperty(x => x.Version, Guid.NewGuid()), token);
        if (count != 1) return new(TrainingOperationStatus.Conflict);
        if (refuse)
        {
            var fill = await promotion.FillMomentInsideTransactionAsync(moment.Id, token);
            if (!fill.Succeeded) return fill;
        }
        return new(schedule.CanEdit(moment) ? TrainingOperationStatus.Success : TrainingOperationStatus.Conflict, id);
    }, ct);
}
