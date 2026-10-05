using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AalstAcademie.Web.Services.Enrolment;

/// <summary>Interne FIFO-conversie onder de bestaande writer-lock; opent of commit nooit een eigen transactie.</summary>
public sealed class WaitlistPromotionService(ApplicationDbContext context, ParticipationEligibility eligibility,
    TrainingSchedule schedule, TimeProvider clock)
{
    internal async Task<TrainingOperationResult> FillMomentInsideTransactionAsync(int momentId, CancellationToken ct)
    {
        RequireTransaction();
        var moment = await context.TrainingMoments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == momentId, ct);
        if (moment is null) return new(TrainingOperationStatus.NotFound);
        var initial = await eligibility.ReadAsync([moment.TrainingId], ct);
        if (!schedule.CanEdit(moment) || initial.Trainings[moment.TrainingId].AudienceScope == TrainingAudienceScope.Unassigned)
            return new(TrainingOperationStatus.Success);
        // De concrete IDs/versies liggen vast; een later opnieuw aangesloten rij wordt nooit via user-ID geadopteerd.
        var candidates = initial.Waitlist.Where(x => x.TrainingId == moment.TrainingId && x.ClosedAtUtc is null).ToArray();
        var assigned = false;
        foreach (var candidate in candidates)
        {
            var state = await eligibility.ReadAsync([moment.TrainingId], ct);
            var current = state.Waitlist.SingleOrDefault(x => x.Id == candidate.Id && x.Version == candidate.Version && x.ClosedAtUtc is null);
            if (eligibility.Availability(moment, state).PhysicalAvailable == 0) break;
            var training = state.Trainings[moment.TrainingId];
            if (current is null || !state.EligibleDepartments.TryGetValue(current.ApplicationUserId, out var department) ||
                !eligibility.Allows(department, training) ||
                eligibility.HasActiveParticipation(current.ApplicationUserId, training.Id, state, current.Id) ||
                eligibility.WasRefused(current.ApplicationUserId, moment.Id, state)) continue;
            var motivation = ParticipationEligibility.ValidateMotivation(current.Motivation, training.RequiresMotivation);
            if (!motivation.Succeeded) continue;
            if (!schedule.CanEdit(moment)) return new(TrainingOperationStatus.Conflict);
            var now = clock.GetUtcNow().UtcDateTime;
            context.Registrations.Add(new Registration { ApplicationUserId = current.ApplicationUserId,
                TrainingMomentId = moment.Id, Status = RegistrationStatus.Requested, Motivation = motivation.Value,
                RequestedAtUtc = now, Version = Guid.NewGuid() });
            await context.SaveChangesAsync(ct);
            var closed = await context.WaitlistEntries.Where(x => x.Id == current.Id && x.Version == current.Version && x.ClosedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClosedAtUtc, now).SetProperty(x => x.Version, Guid.NewGuid()), ct);
            if (closed != 1) return new(TrainingOperationStatus.Conflict);
            assigned = true;
        }
        // Een late startoverschrijding draait óók de reeds uitgevoerde inserts en de veroorzakende write terug.
        return new(assigned && !schedule.CanEdit(moment) ? TrainingOperationStatus.Conflict : TrainingOperationStatus.Success);
    }

    internal async Task<TrainingOperationResult> FillTrainingInsideTransactionAsync(int trainingId, CancellationToken ct)
    {
        RequireTransaction();
        var moments = await context.TrainingMoments.AsNoTracking().Where(x => x.TrainingId == trainingId &&
            x.Status == TrainingMomentStatus.Scheduled).OrderBy(x => x.Date).ThenBy(x => x.StartTime).ThenBy(x => x.Id).ToListAsync(ct);
        var processed = new List<TrainingMoment>();
        foreach (var moment in moments.Where(schedule.CanEdit))
        {
            var result = await FillMomentInsideTransactionAsync(moment.Id, ct);
            if (!result.Succeeded) return result;
            processed.Add(moment);
        }
        return new(processed.Any(x => !schedule.CanEdit(x)) ? TrainingOperationStatus.Conflict : TrainingOperationStatus.Success);
    }

    private void RequireTransaction()
    {
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        if (transaction is not SqliteTransaction || transaction.Connection != context.Database.GetDbConnection())
            throw new InvalidOperationException("Bufferverwerking vereist de bestaande SQLite-schrijftransactie van dezelfde context.");
    }
}
