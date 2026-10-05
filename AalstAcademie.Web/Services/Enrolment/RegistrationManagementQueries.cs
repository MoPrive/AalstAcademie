using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Enrolment;

/// <summary>Read-only beheerprojecties met actuele scope, aparte pagina20 en actuele contact/afdeling/managergegevens.</summary>
public sealed class RegistrationManagementQueries(ApplicationDbContext context, TrainingAccessReader access, TrainingSchedule schedule)
{
    public async Task<RegistrationManagementReadModel> GetAsync(string? actorId, int? trainingId = null, int? momentId = null,
        RegistrationStatus? status = null, int registrationPage = 1, int waitlistPage = 1, CancellationToken ct = default)
    {
        await using var read = context.Database.CurrentTransaction is null ? await context.Database.BeginTransactionAsync(ct) : null;
        var actor = await access.ReadActorAsync(actorId, ct);
        RegistrationManagementReadModel Failure(TrainingOperationStatus value) => new(value, [], [], 1, 1, 1, 1);
        if (actor is null) return Failure(TrainingOperationStatus.Forbidden);
        if (status is not null && !Enum.IsDefined(status.Value)) return Failure(TrainingOperationStatus.Invalid);
        var trainings = context.Trainings.AsNoTracking().Where(x => actor.IsAdministrator || x.InstructorUserId == actor.UserId);
        if (momentId is not null)
        {
            var moment = await context.TrainingMoments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == momentId, ct);
            if (moment is null || !await trainings.AnyAsync(x => x.Id == moment.TrainingId, ct) ||
                trainingId is not null && trainingId != moment.TrainingId) return Failure(TrainingOperationStatus.NotFound);
            trainingId = moment.TrainingId;
        }
        if (trainingId is not null && !await trainings.AnyAsync(x => x.Id == trainingId, ct)) return Failure(TrainingOperationStatus.NotFound);
        var ids = await trainings.Where(x => trainingId == null || x.Id == trainingId).Select(x => x.Id).ToListAsync(ct);
        var registrations = context.Registrations.AsNoTracking().Where(x => ids.Contains(x.TrainingMoment.TrainingId) &&
            (momentId == null || x.TrainingMomentId == momentId) && (status == null || x.Status == status));
        var buffers = context.WaitlistEntries.AsNoTracking().Where(x => ids.Contains(x.TrainingId) && x.ClosedAtUtc == null);
        var rp = Math.Max(1, (await registrations.CountAsync(ct) + 19) / 20);
        var wp = Math.Max(1, (await buffers.CountAsync(ct) + 19) / 20);
        registrationPage = Math.Clamp(registrationPage, 1, rp); waitlistPage = Math.Clamp(waitlistPage, 1, wp);
        var rows = await RegistrationRows(registrations.OrderBy(x => x.TrainingMoment.Date).ThenBy(x => x.TrainingMoment.StartTime)
            .ThenBy(x => x.RequestedAtUtc).ThenBy(x => x.Id).Skip((registrationPage - 1) * 20).Take(20)).ToListAsync(ct);
        rows = rows.Select(x => Flags(x, actor)).ToList();
        var waitlist = await buffers.OrderBy(x => x.JoinedAtUtc).ThenBy(x => x.Id).Skip((waitlistPage - 1) * 20).Take(20)
            .Select(x => new RegistrationManagementBufferRow(x.Id, x.TrainingId, x.Training.Title,
                (x.User.FirstName + " " + x.User.LastName).Trim(), x.User.Email, x.User.PhoneNumber,
                x.User.Department == null ? null : x.User.Department.Name,
                x.User.Manager == null ? null : (x.User.Manager.FirstName + " " + x.User.Manager.LastName).Trim(),
                x.Motivation, x.JoinedAtUtc)).ToListAsync(ct);
        return new(TrainingOperationStatus.Success, rows, waitlist, registrationPage, rp, waitlistPage, wp, trainingId, momentId, status);
    }
    public async Task<RegistrationManagementDetailResult> GetDetailsAsync(string? actorId, int id, CancellationToken ct = default)
    {
        var actor = await access.ReadActorAsync(actorId, ct);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var row = await RegistrationRows(context.Registrations.AsNoTracking().Where(x => x.Id == id &&
            (actor.IsAdministrator || x.TrainingMoment.Training.InstructorUserId == actor.UserId))).SingleOrDefaultAsync(ct);
        return row is null ? new(TrainingOperationStatus.NotFound) : new(TrainingOperationStatus.Success, Flags(row, actor));
    }
    private RegistrationManagementRow Flags(RegistrationManagementRow row, TrainingActor actor) => row with
    {
        CanConfirm = row.CanConfirm && schedule.IsBeforeStoredStart(row.Date, row.StartTime),
        CanRefuse = row.CanRefuse && schedule.IsBeforeStoredStart(row.Date, row.StartTime) &&
            (row.Status == RegistrationStatus.Requested || actor.IsAdministrator && row.Status == RegistrationStatus.Confirmed)
    };
    private static IQueryable<RegistrationManagementRow> RegistrationRows(IQueryable<Registration> rows) => rows.Select(x =>
        new RegistrationManagementRow(x.Id, x.TrainingMomentId, x.TrainingMoment.TrainingId, x.TrainingMoment.Training.Title,
            (x.User.FirstName + " " + x.User.LastName).Trim(), x.User.Email, x.User.PhoneNumber,
            x.User.Department == null ? null : x.User.Department.Name,
            x.User.Manager == null ? null : (x.User.Manager.FirstName + " " + x.User.Manager.LastName).Trim(),
            x.TrainingMoment.Date, x.TrainingMoment.StartTime, x.Status, x.Motivation, x.DecisionReason, x.RequestedAtUtc,
            x.Version, x.TrainingMoment.Version, x.TrainingMoment.Status == TrainingMomentStatus.Scheduled && x.Status == RegistrationStatus.Requested,
            x.TrainingMoment.Status == TrainingMomentStatus.Scheduled && (x.Status == RegistrationStatus.Requested || x.Status == RegistrationStatus.Confirmed)));
}
