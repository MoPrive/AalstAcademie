using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Data;

/// <summary>Zes gewone workflowacties na de004-seed; iedere actie gebruikt haar eigen normale writertransactie.</summary>
public sealed class DemoParticipationWorkflowSeeder(ApplicationDbContext context, DemoMode mode, TimeProvider clock,
    TrainingSchedule schedule, RegistrationReviewService review, RegistrationCancellationService cancellation,
    TrainingMomentManagementService moments, EmployeeEnrolmentService enrolments)
{
    public const int AdditionalMomentId = 300407;
    private const string EmployeeId = "demo-002-employee";
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (!mode.IsEnabled) return;
        if (context.Database.CurrentTransaction is not null) throw new InvalidOperationException("De workflowdemo vereist afzonderlijke normale transacties.");
        var excel = await RegistrationAsync(EmployeeId, 300401, ct);
        Ensure(await review.ConfirmAsync(DemoAccountCatalog.AdministratorId, excel.Id,
            new() { ExpectedRegistrationVersion = excel.Version, ExpectedMomentVersion = excel.TrainingMoment.Version }, ct));
        var external = await RegistrationAsync(EmployeeId, 300406, ct);
        Ensure(await review.RefuseAsync(DemoAccountCatalog.AdministratorId, external.Id,
            new() { ExpectedRegistrationVersion = external.Version, ExpectedMomentVersion = external.TrainingMoment.Version,
                DecisionReason = "Fictieve demo: kies een later opleidingsmoment." }, ct));
        var promoted = await RegistrationAsync(DemoAccountCatalog.InternalInstructorId, 300406, ct);
        Ensure(await cancellation.CancelAsync(DemoAccountCatalog.InternalInstructorId, promoted.Id,
            new() { ExpectedRegistrationVersion = promoted.Version, ExpectedMomentVersion = promoted.TrainingMoment.Version }, ct));
        // Bootstrap heeft een exclusieve applicatielease en nog geen HTTP. Een sleutelbotsing of afwijkende sequence wordt nooit geadopteerd.
        var sequence = await context.Database.SqlQueryRaw<long>("SELECT seq AS Value FROM sqlite_sequence WHERE name = 'TrainingMoments'").SingleAsync(ct);
        if (sequence != 300406 || await context.TrainingMoments.AnyAsync(x => x.Id == AdditionalMomentId, ct))
            throw new InvalidOperationException("De gegenereerde demo-momentsleutel is niet beschikbaar.");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), schedule.TimeZone).DateTime);
        var created = await moments.CreateAsync(DemoAccountCatalog.AdministratorId,
            new(300302, new(today.AddDays(11), new TimeOnly(10, 0), new TimeOnly(12, 0), 300202, 1)), ct);
        Ensure(created);
        if (created.Id != AdditionalMomentId) throw new InvalidOperationException("De normale create gaf een onverwachte demo-momentsleutel.");
        var moment = await context.TrainingMoments.AsNoTracking().Include(x => x.Training).SingleAsync(x => x.Id == AdditionalMomentId, ct);
        Ensure(await enrolments.RequestAsync(DemoAccountCatalog.InternalInstructorId, moment.Id,
            new() { ExpectedTrainingVersion = moment.Training.Version, ExpectedMomentVersion = moment.Version,
                Motivation = "Ik wil duidelijker communiceren met collega's." }, ct));
        Ensure(await enrolments.JoinAsync(EmployeeId, moment.Id,
            new EmployeeWaitlistInput { ExpectedTrainingVersion = moment.Training.Version, ExpectedMomentVersion = moment.Version,
                Motivation = "Ik wil deze opleiding volgen zodra een plaats vrijkomt.", Consent = true }, ct));
        // Een late fout kan eerdere normale commits laten staan. Bootstrap publiceert dan geen gereedheid en de volgende D78-start herbouwt.
    }
    private Task<Registration> RegistrationAsync(string userId, int momentId, CancellationToken ct) =>
        context.Registrations.AsNoTracking().Include(x => x.TrainingMoment).SingleAsync(x => x.ApplicationUserId == userId &&
            x.TrainingMomentId == momentId && x.Status == RegistrationStatus.Requested, ct);
    private static void Ensure(TrainingOperationResult result)
    { if (!result.Succeeded) throw new InvalidOperationException("Een normale demo-workflowactie kon niet veilig worden uitgevoerd."); }
}
