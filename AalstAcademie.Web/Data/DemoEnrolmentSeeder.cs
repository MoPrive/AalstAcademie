using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
namespace AalstAcademie.Web.Data;

/// <summary>Een kleine aanvullende demobatch; iedere deelname gebruikt de normale actuele guards en eigen transactie.</summary>
public sealed class DemoEnrolmentSeeder(ApplicationDbContext context, DemoMode mode, TimeProvider clock,
    TrainingSchedule schedule, TrainingWriteTransaction transaction, EmployeeEnrolmentService enrolments)
{
    public const int AdditionalMomentId = 300406;
    private const string EmployeeId = "demo-002-employee";
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!mode.IsEnabled) return;
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("De deelname-demo vereist afzonderlijke schrijftransacties.");
        // Het gereserveerde ID wordt onder de write-lock gecontroleerd. Een bestaande rij wordt nooit geadopteerd.
        var created = await transaction.ExecuteAsync(async token =>
        {
            if (await context.TrainingMoments.AnyAsync(x => x.Id == AdditionalMomentId, token))
                return new(TrainingOperationStatus.Conflict);
            var training = await context.Trainings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 300302, token);
            var owner = training is null ? null : await new TrainingAccessReader(context, mode).ReadEligibleOwnerAsync(training.InstructorUserId, token);
            if (training is null || training.InstructorUserId != DemoAccountCatalog.ExternalInstructorId || owner?.IsExternal != true)
                return new(TrainingOperationStatus.Conflict);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), schedule.TimeZone).DateTime);
            var fields = new TrainingValueValidation(schedule).ValidateMomentFields(
                new(today.AddDays(10), new TimeOnly(10, 0), new TimeOnly(12, 0), 300202, 1));
            if (!fields.Succeeded) return new(TrainingOperationStatus.Invalid);
            var value = fields.Fields!;
            var room = await context.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == value.LocationId, token);
            if (room is null || value.MaximumParticipants > room.MaximumCapacity ||
                await context.TrainingMoments.AnyAsync(x => x.Status == TrainingMomentStatus.Scheduled &&
                    x.LocationId == room.Id && x.Date == value.Date && value.StartTime < x.EndTime && x.StartTime < value.EndTime, token))
                return new(TrainingOperationStatus.Conflict);
            context.TrainingMoments.Add(new TrainingMoment { Id = AdditionalMomentId, TrainingId = training.Id,
                LocationId = room.Id, Date = value.Date, StartTime = value.StartTime, EndTime = value.EndTime,
                MaximumParticipants = value.MaximumParticipants, Status = TrainingMomentStatus.Scheduled, Version = Guid.NewGuid() });
            await context.SaveChangesAsync(token);
            return new(TrainingOperationStatus.Success, AdditionalMomentId);
        }, ct);
        Ensure(created, "Het aanvullende demo-moment kon niet veilig worden opgebouwd.");
        // De momentbatch is al gecommit. Geen geneste write-lock of publieke seed-bypass.
        var excel = await VersionsAsync(300401, ct);
        Ensure(await enrolments.RequestAsync(EmployeeId, 300401,
            new() { ExpectedTrainingVersion = excel.TrainingVersion, ExpectedMomentVersion = excel.MomentVersion }, ct),
            "De eerste demo-aanvraag kon niet worden opgebouwd.");
        var external = await VersionsAsync(AdditionalMomentId, ct);
        Ensure(await enrolments.RequestAsync(EmployeeId, AdditionalMomentId,
            new() { ExpectedTrainingVersion = external.TrainingVersion, ExpectedMomentVersion = external.MomentVersion,
                Motivation = "Ik wil duidelijker samenwerken met collega's." }, ct), "De externe demo-aanvraag kon niet worden opgebouwd.");
        Ensure(await enrolments.JoinAsync(DemoAccountCatalog.InternalInstructorId, AdditionalMomentId,
            new EmployeeWaitlistInput { ExpectedTrainingVersion = external.TrainingVersion, ExpectedMomentVersion = external.MomentVersion,
                Consent = true, Motivation = "Ik wil mijn communicatievaardigheden verbeteren." }, ct),
            "De demo-wachtlijstaansluiting kon niet worden opgebouwd.");
        // Een late failure maakt eerdere commits niet ongedaan. Bootstrap meldt dan eerlijk geen gereedheid.
    }
    private async Task<(Guid TrainingVersion, Guid MomentVersion)> VersionsAsync(int id, CancellationToken ct)
    {
        var moment = await context.TrainingMoments.AsNoTracking().Include(x => x.Training).SingleAsync(x => x.Id == id, ct);
        return (moment.Training.Version, moment.Version);
    }
    private static void Ensure(TrainingOperationResult result, string message)
    { if (!result.Succeeded) throw new InvalidOperationException(message); }
}
