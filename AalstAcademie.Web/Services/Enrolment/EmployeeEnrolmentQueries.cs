using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Enrolment;

/// <summary>Eigen historie gebruikt de algemene Approved-grens; later rol- of doelgroepverlies wist geen inzicht of intrekrecht.</summary>
public sealed class EmployeeEnrolmentQueries(ApplicationDbContext context, EmployeeAccessReader access, TrainingSchedule schedule)
{
    public const int PageSize = 20;
    public async Task<OwnRegistrationsReadModel> GetAsync(string? actorId, int registrationPage = 1, int waitlistPage = 1, CancellationToken ct = default)
    {
        await using var read = context.Database.CurrentTransaction is null ? await context.Database.BeginTransactionAsync(ct) : null;
        if (await access.ReadApprovedAsync(actorId, ct) is null) return new(TrainingOperationStatus.Forbidden, [], [], 1, 1, 1, 1);
        var registrations = context.Registrations.AsNoTracking().Where(x => x.ApplicationUserId == actorId);
        var buffers = context.WaitlistEntries.AsNoTracking().Where(x => x.ApplicationUserId == actorId);
        var registrationPages = Math.Max(1, (await registrations.CountAsync(ct) + 19) / 20);
        var bufferPages = Math.Max(1, (await buffers.CountAsync(ct) + 19) / 20);
        registrationPage = Math.Clamp(registrationPage, 1, registrationPages); waitlistPage = Math.Clamp(waitlistPage, 1, bufferPages);
        var rows = await registrations.OrderByDescending(x => x.RequestedAtUtc).ThenByDescending(x => x.Id)
            .Skip((registrationPage - 1) * 20).Take(20).Select(x => new OwnRegistrationRow(x.Id, x.TrainingMomentId,
                x.TrainingMoment.TrainingId, x.TrainingMoment.Training.Title, x.TrainingMoment.Training.Category.Name,
                context.ExternalInstructors.Where(p => p.ApplicationUserId == x.TrainingMoment.Training.InstructorUserId).Select(p => p.OrganizationName)
                    .FirstOrDefault() ?? (x.TrainingMoment.Training.Instructor.FirstName + " " + x.TrainingMoment.Training.Instructor.LastName),
                x.TrainingMoment.Location.Name, x.TrainingMoment.Location.Address, x.TrainingMoment.Date, x.TrainingMoment.StartTime,
                x.TrainingMoment.EndTime, x.TrainingMoment.Training.ExternalTotalPriceEuros, x.Status, x.Motivation, x.DecisionReason, x.RequestedAtUtc)).ToListAsync(ct);
        var rowMomentIds = rows.Select(x => x.MomentId).Distinct().ToArray();
        var cancellableMoments = await context.TrainingMoments.AsNoTracking().Where(x => rowMomentIds.Contains(x.Id) &&
            x.Status == TrainingMomentStatus.Scheduled).Select(x => x.Id).ToListAsync(ct);
        rows = rows.Select(x => x with { CanCancel = x.Status is RegistrationStatus.Requested or RegistrationStatus.Confirmed &&
            cancellableMoments.Contains(x.MomentId) && schedule.IsBeforeStoredStart(x.Date, x.StartTime) }).ToList();
        var waitlist = await BufferRows(buffers.OrderByDescending(x => x.JoinedAtUtc).ThenByDescending(x => x.Id)
            .Skip((waitlistPage - 1) * 20).Take(20)).ToListAsync(ct);
        return new(TrainingOperationStatus.Success, rows, waitlist, registrationPage, registrationPages, waitlistPage, bufferPages);
    }

    public async Task<OwnWithdrawReadResult> GetWithdrawAsync(string? actorId, int entryId, CancellationToken ct = default)
    {
        if (await access.ReadApprovedAsync(actorId, ct) is null) return new(TrainingOperationStatus.Forbidden);
        var row = await BufferRows(context.WaitlistEntries.AsNoTracking().Where(x => x.Id == entryId && x.ApplicationUserId == actorId)).SingleOrDefaultAsync(ct);
        return row is null ? new(TrainingOperationStatus.NotFound) : new(TrainingOperationStatus.Success, row);
    }

    public async Task<OwnRegistrationCancelReadResult> GetCancelAsync(string? actorId, int id, CancellationToken ct = default)
    {
        if (await access.ReadApprovedAsync(actorId, ct) is null) return new(TrainingOperationStatus.Forbidden);
        var row = await context.Registrations.AsNoTracking().Include(x => x.TrainingMoment).ThenInclude(x => x.Training)
            .SingleOrDefaultAsync(x => x.Id == id && x.ApplicationUserId == actorId, ct);
        if (row is null) return new(TrainingOperationStatus.NotFound);
        var m = row.TrainingMoment;
        return new(TrainingOperationStatus.Success, new(row.Id, m.Id, m.TrainingId, m.Training.Title, m.Date, m.StartTime,
            row.Status, row.Motivation, row.DecisionReason, row.Version, m.Version,
            row.Status is RegistrationStatus.Requested or RegistrationStatus.Confirmed && schedule.CanEdit(m)));
    }

    private IQueryable<OwnWaitlistRow> BufferRows(IQueryable<AalstAcademie.Web.Models.Domain.WaitlistEntry> query) => query.Select(x => new OwnWaitlistRow(
        x.Id, x.TrainingId, x.Training.Title, x.Training.Category.Name,
        context.ExternalInstructors.Where(p => p.ApplicationUserId == x.Training.InstructorUserId).Select(p => p.OrganizationName).FirstOrDefault() ??
            (x.Training.Instructor.FirstName + " " + x.Training.Instructor.LastName), x.Training.ExternalTotalPriceEuros,
        x.Motivation, x.JoinedAtUtc, x.ClosedAtUtc, x.Version));
}
