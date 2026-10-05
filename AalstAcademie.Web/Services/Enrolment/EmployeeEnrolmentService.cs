using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Enrolment;

/// <summary>Drie expliciete acties: Requested-aanvraag, opleidingsbuffer en concrete bufferintrekking. Alleen intrekking heeft een gerichte interne verwerkingshook.</summary>
public sealed class EmployeeEnrolmentService(ApplicationDbContext context, EmployeeAccessReader access,
    ParticipationEligibility eligibility, TrainingWriteTransaction transaction, TimeProvider clock, WaitlistPromotionService promotion)
{
    public Task<TrainingOperationResult> RequestAsync(string? actorId, int momentId, EmployeeRequestInput input,
        CancellationToken ct = default) => ParticipateAsync(actorId, momentId, input, null, ct);
    public Task<TrainingOperationResult> JoinAsync(string? actorId, int momentId, EmployeeWaitlistInput input,
        CancellationToken ct = default) => ParticipateAsync(actorId, momentId, input, input, ct);

    private Task<TrainingOperationResult> ParticipateAsync(string? actorId, int momentId, EmployeeRequestInput input,
        EmployeeWaitlistInput? join, CancellationToken ct) => transaction.ExecuteAsync(async token =>
    {
        // Alle guardreads vinden na BEGIN IMMEDIATE plaats. Een eerdere GET/cookie kan geen recht of plaats reserveren.
        var actor = await access.ReadEmployeeAsync(actorId, token);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var moment = await context.TrainingMoments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == momentId, token);
        if (moment is null) return new(TrainingOperationStatus.NotFound);
        var state = await eligibility.ReadAsync([moment.TrainingId], token);
        var training = state.Trainings[moment.TrainingId];
        if (!eligibility.Allows(actor.DepartmentId!.Value, training)) return new(TrainingOperationStatus.Forbidden);
        var errors = join is null ? ParticipationEligibility.ValidateVersions(input) : ParticipationEligibility.ValidateWaitlistInput(join);
        var motivation = ParticipationEligibility.ValidateMotivation(input.Motivation, training.RequiresMotivation);
        var combined = new Dictionary<string, string[]>(errors);
        foreach (var error in motivation.Errors) combined[error.Key] = error.Value;
        if (combined.Count != 0) return new(TrainingOperationStatus.Invalid, Errors: combined);
        if (training.Version != input.ExpectedTrainingVersion || moment.Version != input.ExpectedMomentVersion ||
            !eligibility.IsNewMoment(moment) || eligibility.HasActiveParticipation(actor.UserId, training.Id, state) ||
            eligibility.WasRefused(actor.UserId, moment.Id, state)) return new(TrainingOperationStatus.Conflict);
        var available = eligibility.Availability(moment, state).DirectAvailable;
        // Een volle keuze wordt nooit stil een aanvraag of buffer voor een ander moment.
        if ((join is null && available == 0) || (join is not null && available > 0)) return new(TrainingOperationStatus.Conflict);
        if (!eligibility.IsNewMoment(moment)) return new(TrainingOperationStatus.Conflict); // klok vlak vóór insert opnieuw lezen
        var now = clock.GetUtcNow().UtcDateTime;
        int id;
        if (join is null)
        {
            var row = new Registration { ApplicationUserId = actor.UserId, TrainingMomentId = momentId,
                Status = RegistrationStatus.Requested, Motivation = motivation.Value, RequestedAtUtc = now, Version = Guid.NewGuid() };
            context.Registrations.Add(row); await context.SaveChangesAsync(token); id = row.Id;
        }
        else
        {
            var row = new WaitlistEntry { ApplicationUserId = actor.UserId, TrainingId = training.Id,
                Motivation = motivation.Value, JoinedAtUtc = now, ClosedAtUtc = null, Version = Guid.NewGuid() };
            context.WaitlistEntries.Add(row); await context.SaveChangesAsync(token); id = row.Id;
        }
        return new(TrainingOperationStatus.Success, id);
    }, ct);

    public Task<TrainingOperationResult> WithdrawAsync(string? actorId, int entryId, EmployeeWithdrawInput input,
        CancellationToken ct = default) => transaction.ExecuteAsync(async token =>
    {
        if (await access.ReadApprovedAsync(actorId, token) is null) return new(TrainingOperationStatus.Forbidden);
        var row = await context.WaitlistEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId && x.ApplicationUserId == actorId, token);
        if (row is null) return new(TrainingOperationStatus.NotFound);
        if (input.ExpectedVersion is null || input.ExpectedVersion == Guid.Empty) return new(TrainingOperationStatus.Invalid);
        if (row.ClosedAtUtc is not null || row.Version != input.ExpectedVersion) return new(TrainingOperationStatus.Conflict);
        var now = clock.GetUtcNow().UtcDateTime;
        var count = await context.WaitlistEntries.Where(x => x.Id == entryId && x.ApplicationUserId == actorId &&
            x.ClosedAtUtc == null && x.Version == input.ExpectedVersion).ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.ClosedAtUtc, now).SetProperty(x => x.Version, Guid.NewGuid()), token);
        if (count != 1) return new(TrainingOperationStatus.Conflict, entryId);
        var fill = await promotion.FillTrainingInsideTransactionAsync(row.TrainingId, token);
        return fill.Succeeded ? new(TrainingOperationStatus.Success, entryId) : fill;
    }, ct);
}
