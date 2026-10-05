using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
namespace AalstAcademie.Tests.Data;

/// <summary>De echte bootstrap moet de complete fictieve deelnamebatch leveren vóór de gereedmelding.</summary>
public class DemoEnrolmentSeederTests
{
    [Theory]
    [InlineData("ExactSixAccountsFourTrainingsSixMomentsTwoRequestsOneBuffer")]
    [InlineData("ManagersJulesNoorSelfRobinNull")]
    [InlineData("Moment300406DateRoomMaxAndOverlap")]
    [InlineData("OriginalFiveMomentsAndMaximaUnchanged")]
    [InlineData("ReservedMomentCollisionRefused")]
    [InlineData("RequestFailureNoReady")]
    [InlineData("JoinFailureNoReadyAndPriorCommitsReportedHonestly")]
    [InlineData("NonDemoNoEnrolmentSeed")]
    public async Task Complete_seed(string variant)
    {
        await using var environment = new DemoStartupTestEnvironment();
        var failure = new DemoStartupFailureInterceptor();
        var failing = variant.StartsWith("RequestFailure", StringComparison.Ordinal) || variant.StartsWith("JoinFailure", StringComparison.Ordinal);
        if (failing) failure.Arm(variant.StartsWith("Request", StringComparison.Ordinal) ? "request" : "join");
        await using var provider = environment.CreateServices(demoEnabled: variant != "NonDemoNoEnrolmentSeed", interceptor: failure, suppressWorkflowSeed: true);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var bootstrap = scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>();
        if (failing)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => bootstrap.InitializeAsync());
            Assert.True(failure.Triggered && failure.SawSqlWrite); Assert.False(provider.GetRequiredService<DemoCredentials>().IsReady);
            db.ChangeTracker.Clear();
            Assert.Equal(6, await db.TrainingMoments.CountAsync());
            Assert.Equal(variant.StartsWith("Request", StringComparison.Ordinal) ? 0 : 2, await db.Registrations.CountAsync());
            Assert.Equal(0, await db.WaitlistEntries.CountAsync()); return;
        }
        await bootstrap.InitializeAsync();
        // ExecuteUpdate in de accountbatch wijzigt opgeslagen FKs buiten de oude tracker; bewijs leest een nieuwe scope.
        await using var readScope = provider.CreateAsyncScope();
        db = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (variant == "NonDemoNoEnrolmentSeed")
        {
            var before = await TrainingTestData.DigestAsync(scope.ServiceProvider);
            await Seeder(db, new DemoMode(false), environment.Clock).SeedAsync();
            Assert.Equal(before, await TrainingTestData.DigestAsync(scope.ServiceProvider));
            Assert.Equal(0, await db.Users.CountAsync()); Assert.Equal(0, await db.TrainingMoments.CountAsync());
            Assert.False(provider.GetRequiredService<DemoCredentials>().IsReady); return;
        }
        Assert.Equal(6, await db.Users.CountAsync());
        Assert.Equal(4, await db.Trainings.CountAsync());
        Assert.Equal(6, await db.TrainingMoments.CountAsync());
        Assert.Equal(2, await db.Registrations.CountAsync());
        Assert.Equal(1, await db.WaitlistEntries.CountAsync());
        Assert.True(provider.GetRequiredService<DemoCredentials>().IsReady);
        Assert.Equal(4, await db.Departments.CountAsync()); Assert.Equal(3, await db.Roles.CountAsync());
        var requests = await db.Registrations.AsNoTracking().OrderBy(x => x.TrainingMomentId).ToArrayAsync();
        Assert.All(requests, x => { Assert.Equal(RegistrationStatus.Requested, x.Status); Assert.Equal("demo-002-employee", x.ApplicationUserId); Assert.NotEqual(Guid.Empty, x.Version); Assert.Equal(environment.Clock.GetUtcNow().UtcDateTime, x.RequestedAtUtc); });
        Assert.Equal(new[] { 300401, 300406 }, requests.Select(x => x.TrainingMomentId));
        Assert.Null(requests[0].Motivation); Assert.Equal("Ik wil duidelijker samenwerken met collega's.", requests[1].Motivation);
        var buffer = await db.WaitlistEntries.AsNoTracking().SingleAsync();
        Assert.Equal(DemoAccountCatalog.InternalInstructorId, buffer.ApplicationUserId); Assert.Equal(300302, buffer.TrainingId);
        Assert.Null(buffer.ClosedAtUtc); Assert.NotEqual(Guid.Empty, buffer.Version); Assert.Equal(environment.Clock.GetUtcNow().UtcDateTime, buffer.JoinedAtUtc);
        Assert.Equal("Ik wil mijn communicatievaardigheden verbeteren.", buffer.Motivation);
        if (variant == "ManagersJulesNoorSelfRobinNull")
        {
            Assert.Equal(DemoAccountCatalog.InternalInstructorId, (await db.Departments.SingleAsync(x => x.Name == "ICT")).ResponsibleUserId);
            Assert.Equal(DemoAccountCatalog.InternalInstructorId, (await db.Users.SingleAsync(x => x.Id == "demo-002-employee")).ManagerUserId);
            Assert.Equal(DemoAccountCatalog.InternalInstructorId, (await db.Users.SingleAsync(x => x.Id == DemoAccountCatalog.InternalInstructorId)).ManagerUserId);
            Assert.Null((await db.Users.SingleAsync(x => x.Id == "demo-005-pending-employee")).ManagerUserId);
        }
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(environment.Clock.GetUtcNow(), new TrainingSchedule(environment.Clock).TimeZone).DateTime);
        if (variant == "Moment300406DateRoomMaxAndOverlap")
        {
            var moment = await db.TrainingMoments.AsNoTracking().SingleAsync(x => x.Id == 300406);
            Assert.Equal(today.AddDays(10), moment.Date); Assert.Equal(new TimeOnly(10, 0), moment.StartTime); Assert.Equal(new TimeOnly(12, 0), moment.EndTime);
            Assert.Equal(300202, moment.LocationId); Assert.Equal(300302, moment.TrainingId); Assert.Equal(1, moment.MaximumParticipants);
            Assert.Equal(TrainingMomentStatus.Scheduled, moment.Status); Assert.NotEqual(Guid.Empty, moment.Version);
            Assert.False(await db.TrainingMoments.AnyAsync(x => x.Id != moment.Id && x.LocationId == moment.LocationId && x.Date == moment.Date && moment.StartTime < x.EndTime && x.StartTime < moment.EndTime));
        }
        if (variant == "OriginalFiveMomentsAndMaximaUnchanged")
        {
            int[] offsets = [7, 7, 8, 9, -7]; int[] maxima = [16, 16, 10, 12, 12]; int[] starts = [9, 11, 10, 9, 9]; int[] ends = [11, 13, 12, 10, 10];
            for (var i = 0; i < 5; i++)
            { var moment = await db.TrainingMoments.SingleAsync(x => x.Id == 300401 + i); Assert.Equal(today.AddDays(offsets[i]), moment.Date); Assert.Equal(maxima[i], moment.MaximumParticipants); Assert.Equal(new TimeOnly(starts[i], 0), moment.StartTime); Assert.Equal(new TimeOnly(ends[i], 0), moment.EndTime); }
            Assert.Equal(3, await db.Categories.CountAsync()); Assert.Equal(3, await db.Locations.CountAsync());
        }
        if (variant == "ReservedMomentCollisionRefused")
        {
            var before = await TrainingTestData.DigestAsync(scope.ServiceProvider);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Seeder(db, new DemoMode(true), environment.Clock).SeedAsync());
            Assert.Equal(before, await TrainingTestData.DigestAsync(scope.ServiceProvider));
        }
    }
    private static DemoEnrolmentSeeder Seeder(ApplicationDbContext db, DemoMode mode, TimeProvider clock)
    {
        var schedule = new TrainingSchedule(clock); var transaction = new TrainingWriteTransaction(db);
        return new(db, mode, clock, schedule, transaction,
            new EmployeeEnrolmentService(db, new EmployeeAccessReader(db, mode), new ParticipationEligibility(db, schedule, mode), transaction, clock, new WaitlistPromotionService(db, new ParticipationEligibility(db, schedule, mode), schedule, clock)));
    }
}
