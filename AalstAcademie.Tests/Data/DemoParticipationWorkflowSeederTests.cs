using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Data;

public sealed class DemoParticipationWorkflowSeederTests
{
    [Theory]
    [InlineData("ExactSixAccountsFourTrainingsSevenMoments")]
    [InlineData("FourRegistrationStatuses")]
    [InlineData("TwoBufferHistoryRows")]
    [InlineData("ExistingFiveMomentsAndMaximaKept")]
    [InlineData("JulesPromotionExactly300406")]
    [InlineData("NewMoment300407CalendarRoomMaxNoOverlap")]
    [InlineData("NonDemoNoWorkflowWrites")]
    [InlineData("SuppressSeedSuppressesWorkflow")]
    public async Task Cases(string variant)
    {
        await using var e = new DemoStartupTestEnvironment(); await using var provider = e.CreateServices(demoEnabled: variant != "NonDemoNoWorkflowWrites", suppressSeed: variant == "SuppressSeedSuppressesWorkflow");
        await using (var write = provider.CreateAsyncScope()) await write.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>().InitializeAsync();
        await using var read = provider.CreateAsyncScope(); var db = read.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (variant is "NonDemoNoWorkflowWrites" or "SuppressSeedSuppressesWorkflow")
        { Assert.Equal(0, await db.Trainings.CountAsync()); Assert.Equal(0, await db.Registrations.CountAsync()); Assert.Equal(0, await db.WaitlistEntries.CountAsync()); Assert.False(provider.GetRequiredService<DemoCredentials>().IsReady); return; }
        if (variant == "TwoBufferHistoryRows")
        { Assert.Equal(2, await db.WaitlistEntries.CountAsync()); Assert.Equal(1, await db.WaitlistEntries.CountAsync(x => x.ClosedAtUtc == null)); Assert.Equal("demo-002-employee", (await db.WaitlistEntries.SingleAsync(x => x.ClosedAtUtc == null)).ApplicationUserId); return; }
        if (variant == "ExistingFiveMomentsAndMaximaKept")
        { Assert.Equal(new[]{16,16,10,12,12}, await db.TrainingMoments.Where(x => x.Id < 300406).OrderBy(x => x.Id).Select(x => x.MaximumParticipants).ToArrayAsync()); return; }
        if (variant == "JulesPromotionExactly300406")
        { var promoted = await db.Registrations.AsNoTracking().SingleAsync(x => x.ApplicationUserId == DemoAccountCatalog.InternalInstructorId && x.TrainingMomentId == 300406); Assert.Equal(RegistrationStatus.Cancelled, promoted.Status); Assert.NotNull((await db.WaitlistEntries.SingleAsync(x => x.ApplicationUserId == DemoAccountCatalog.InternalInstructorId)).ClosedAtUtc); return; }
        if (variant == "NewMoment300407CalendarRoomMaxNoOverlap")
        { var m = await db.TrainingMoments.AsNoTracking().SingleAsync(x => x.Id == 300407); Assert.Equal(300302, m.TrainingId); Assert.Equal(300202, m.LocationId); Assert.Equal(1, m.MaximumParticipants); Assert.Equal(TrainingMomentStatus.Scheduled, m.Status); Assert.Equal(new TimeOnly(10,0), m.StartTime); Assert.Equal(new TimeOnly(12,0), m.EndTime); var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.Clock.GetUtcNow(), read.ServiceProvider.GetRequiredService<AalstAcademie.Web.Services.Training.TrainingSchedule>().TimeZone).DateTime); Assert.Equal(today.AddDays(11), m.Date); Assert.False(await db.TrainingMoments.AnyAsync(x => x.Id != m.Id && x.Status == TrainingMomentStatus.Scheduled && x.LocationId == m.LocationId && x.Date == m.Date && x.StartTime < m.EndTime && m.StartTime < x.EndTime)); return; }
        if (variant == "ExactSixAccountsFourTrainingsSevenMoments")
        { Assert.Equal(6, await db.Users.CountAsync()); Assert.Equal(4, await db.Trainings.CountAsync()); Assert.Equal(7, await db.TrainingMoments.CountAsync()); }
        else
        { Assert.Equal(4, await db.Registrations.CountAsync()); foreach (var status in Enum.GetValues<RegistrationStatus>()) Assert.Equal(1, await db.Registrations.CountAsync(x => x.Status == status)); }
    }
}
