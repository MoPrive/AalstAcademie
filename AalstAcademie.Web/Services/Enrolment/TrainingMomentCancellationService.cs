using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Enrolment;

/// <summary>Alleen admin annuleert het volledige moment. Retour en statuswrites committen samen; andere momenten worden niet gevuld.</summary>
public sealed class TrainingMomentCancellationService(ApplicationDbContext context, TrainingAccessReader access,
    ParticipationEligibility eligibility, TrainingWriteTransaction transaction, TrainingSchedule schedule)
{
    public Task<TrainingOperationResult> CancelAsync(string? actorId, int id, TrainingMomentCancelInput input, CancellationToken ct = default) =>
        transaction.ExecuteAsync(async token =>
        {
            if ((await access.ReadActorAsync(actorId, token))?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
            var moment = await context.TrainingMoments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            if (moment is null) return new(TrainingOperationStatus.NotFound);
            var errors = RegistrationWorkflowValidation.MomentVersion(input.ExpectedMomentVersion);
            if (errors.Count != 0) return new(TrainingOperationStatus.Invalid, Errors: errors);
            if (moment.Version != input.ExpectedMomentVersion || !schedule.CanEdit(moment)) return new(TrainingOperationStatus.Conflict);
            var state = await eligibility.ReadAsync([moment.TrainingId], token);
            var affected = state.Registrations.Where(x => x.TrainingMomentId == id &&
                x.Status is RegistrationStatus.Requested or RegistrationStatus.Confirmed).ToArray();
            // Onveilige oude dubbele deelname wordt niet stil gerepareerd; ook de momentwrite blijft dan uit.
            if (affected.GroupBy(x => x.ApplicationUserId).Any(x => x.Count() > 1)) return new(TrainingOperationStatus.Conflict);
            foreach (var row in affected)
            {
                if (state.Waitlist.Count(x => x.ApplicationUserId == row.ApplicationUserId && x.ClosedAtUtc is null) > 1 ||
                    state.Registrations.Any(x => x.ApplicationUserId == row.ApplicationUserId && x.TrainingMomentId != id &&
                        x.Status is RegistrationStatus.Requested or RegistrationStatus.Confirmed &&
                        state.Moments.Any(m => m.Id == x.TrainingMomentId && m.Status == TrainingMomentStatus.Scheduled &&
                            schedule.IsBeforeStoredEnd(m.Date, m.EndTime)))) return new(TrainingOperationStatus.Conflict);
            }
            if (!schedule.CanEdit(moment)) return new(TrainingOperationStatus.Conflict);
            var changed = await context.TrainingMoments.Where(x => x.Id == id && x.Version == input.ExpectedMomentVersion &&
                x.Status == TrainingMomentStatus.Scheduled).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TrainingMomentStatus.Cancelled)
                    .SetProperty(x => x.Version, Guid.NewGuid()), token);
            if (changed != 1) return new(TrainingOperationStatus.Conflict);
            foreach (var row in affected)
            {
                changed = await context.Registrations.Where(x => x.Id == row.Id && x.Version == row.Version && x.Status == row.Status)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RegistrationStatus.Cancelled).SetProperty(x => x.Version, Guid.NewGuid()), token);
                if (changed != 1) return new(TrainingOperationStatus.Conflict);
                if (!state.Waitlist.Any(x => x.ApplicationUserId == row.ApplicationUserId && x.ClosedAtUtc is null))
                {
                    context.WaitlistEntries.Add(new WaitlistEntry { ApplicationUserId = row.ApplicationUserId, TrainingId = moment.TrainingId,
                        JoinedAtUtc = row.RequestedAtUtc, Motivation = row.Motivation, ClosedAtUtc = null, Version = Guid.NewGuid() });
                    await context.SaveChangesAsync(token);
                }
            }
            return new(schedule.CanEdit(moment) ? TrainingOperationStatus.Success : TrainingOperationStatus.Conflict, id);
        }, ct);
}
