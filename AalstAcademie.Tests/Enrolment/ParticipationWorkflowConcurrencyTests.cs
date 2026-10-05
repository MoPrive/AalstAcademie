using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Enrolment;

/// <summary>Werkelijke writer-lockbarrières, twee fysieke verbindingen en duurzame readbacks van de belangrijkste schrijfparen.</summary>
[Collection("Revision02 provider races")]
public sealed class ParticipationWorkflowConcurrencyTests
{
    [Theory]
    [InlineData("ConfirmVsOwnCancelAFirst")][InlineData("ConfirmVsOwnCancelBFirst")]
    [InlineData("MomentCancelVsRequestAFirst")][InlineData("MomentCancelVsRequestBFirst")]
    [InlineData("WithdrawVsRefillAFirst")][InlineData("WithdrawVsRefillBFirst")]
    [InlineData("MaxIncreaseVsRoomDecreaseAFirst")][InlineData("MaxIncreaseVsRoomDecreaseBFirst")]
    [InlineData("TwoMomentCreatesOneWaiterAFirst")][InlineData("TwoMomentCreatesOneWaiterBFirst")]
    public async Task Cases(string variant)
    {
        await using var f = await EnrolmentRaceFixture.CreateAsync(maximum: 1);
        var aFirst = variant.EndsWith("AFirst", StringComparison.Ordinal);
        Func<IServiceProvider, Task<TrainingOperationResult>> a, b;
        Registration? row = null; WaitlistEntry? buffer = null;
        if (variant.StartsWith("Confirm", StringComparison.Ordinal))
        {
            row = await TrainingTestData.CreateRegistrationAsync(f.Services, f.Moment, applicant: f.Employee);
            a = sp => sp.GetRequiredService<RegistrationReviewService>().ConfirmAsync(f.Owner.Id, row.Id,
                new() { ExpectedRegistrationVersion = row.Version, ExpectedMomentVersion = f.Moment.Version });
            b = sp => sp.GetRequiredService<RegistrationCancellationService>().CancelAsync(f.Employee.Id, row.Id,
                new() { ExpectedRegistrationVersion = row.Version, ExpectedMomentVersion = f.Moment.Version });
        }
        else if (variant.StartsWith("MomentCancel", StringComparison.Ordinal))
        {
            a = sp => sp.GetRequiredService<TrainingMomentCancellationService>().CancelAsync(f.Admin.Id, f.Moment.Id,
                new() { ExpectedMomentVersion = f.Moment.Version });
            b = sp => f.Request(sp);
        }
        else if (variant.StartsWith("Withdraw", StringComparison.Ordinal))
        {
            buffer = await f.BufferAsync(f.Employee);
            a = sp => sp.GetRequiredService<EmployeeEnrolmentService>().WithdrawAsync(f.Employee.Id, buffer.Id, new() { ExpectedVersion = buffer.Version });
            b = sp => sp.GetRequiredService<TrainingMomentManagementService>().ChangeCapacityAsync(f.Owner.Id, new(f.Moment.Id, f.Moment.Version, 2));
        }
        else if (variant.StartsWith("MaxIncrease", StringComparison.Ordinal))
        {
            a = sp => sp.GetRequiredService<TrainingMomentManagementService>().ChangeCapacityAsync(f.Owner.Id, new(f.Moment.Id, f.Moment.Version, 2));
            b = sp => sp.GetRequiredService<LocationManagementService>().UpdateAsync(f.Admin.Id, new(f.Room.Id, f.Room.Version, new(f.Room.Name, f.Room.Address, 1)));
        }
        else
        {
            buffer = await f.BufferAsync(f.Employee);
            a = sp => sp.GetRequiredService<TrainingMomentManagementService>().CreateAsync(f.Owner.Id, new(f.Training.Id,
                new(f.Moment.Date.AddDays(1), f.Moment.StartTime, f.Moment.EndTime, f.Room.Id, 1)));
            b = sp => sp.GetRequiredService<TrainingMomentManagementService>().CreateAsync(f.Owner.Id, new(f.Training.Id,
                new(f.Moment.Date.AddDays(2), f.Moment.StartTime, f.Moment.EndTime, f.Room.Id, 1)));
        }
        var results = await f.Race(aFirst ? a : b, aFirst ? b : a);
        Assert.True(results[0].Succeeded);
        if (variant.StartsWith("Confirm", StringComparison.Ordinal) || variant.StartsWith("MaxIncrease", StringComparison.Ordinal) ||
            variant.StartsWith("MomentCancel", StringComparison.Ordinal) && aFirst || variant.StartsWith("Withdraw", StringComparison.Ordinal) && !aFirst)
            Assert.Equal(TrainingOperationStatus.Conflict, results[1].Status);
        else Assert.True(results[1].Succeeded);
        await f.Read(async db =>
        {
            var moment = await db.TrainingMoments.AsNoTracking().SingleAsync(x => x.Id == f.Moment.Id);
            var room = await db.Locations.AsNoTracking().SingleAsync(x => x.Id == f.Room.Id);
            Assert.True(moment.MaximumParticipants <= room.MaximumCapacity);
            if (variant.StartsWith("Confirm", StringComparison.Ordinal))
                Assert.Equal(aFirst ? RegistrationStatus.Confirmed : RegistrationStatus.Cancelled, (await db.Registrations.SingleAsync()).Status);
            if (variant.StartsWith("MomentCancel", StringComparison.Ordinal))
            {
                Assert.Equal(TrainingMomentStatus.Cancelled, moment.Status);
                Assert.Equal(aFirst ? 0 : 1, await db.WaitlistEntries.CountAsync(x => x.ClosedAtUtc == null));
                Assert.False(await db.Registrations.AnyAsync(x => x.Status == RegistrationStatus.Requested || x.Status == RegistrationStatus.Confirmed));
            }
            if (buffer is not null)
            {
                var saved = await db.WaitlistEntries.AsNoTracking().SingleAsync(x => x.Id == buffer.Id); Assert.NotNull(saved.ClosedAtUtc);
                Assert.Equal(buffer.JoinedAtUtc, saved.JoinedAtUtc); Assert.Equal(buffer.Motivation, saved.Motivation);
                var converted = await db.Registrations.AsNoTracking().Where(x => x.ApplicationUserId == f.Employee.Id).ToListAsync();
                Assert.Equal(variant.StartsWith("Withdraw", StringComparison.Ordinal) && aFirst ? 0 : 1, converted.Count);
                Assert.All(converted, x => Assert.Equal(RegistrationStatus.Requested, x.Status));
            }
        });
    }
}
