using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Enrolment;

/// <summary>De eerste zes gedragsproeven compileren tegen sprint004 en bewijzen de nog ontbrekende writerhooks.</summary>
public sealed class ParticipationWorkflowHookTests
{
    [Theory]
    [InlineData("NewFutureMomentFillsItsBuffer")]
    [InlineData("RaisedMaximumFillsSameMoment")]
    [InlineData("AudienceAssignmentFillsFuture")]
    [InlineData("WithdrawConcreteEntryFillsTraining")]
    [InlineData("CreateFillFailureRollsBackMoment")]
    [InlineData("CapacityFillFailureRollsBackMaximum")]
    public async Task Cases(string variant)
    {
        var fault = new ParticipationWorkflowFailureInterceptor();
        await using var f = await ParticipationWorkflowFixture.CreateAsync(maximum: 1, interceptor: fault);
        var waiter = await f.BufferAsync();
        if (variant.Contains("Maximum")) await f.RegistrationAsync(user: await f.OtherEmployeeAsync());
        WaitlistEntry? withdrawn = null;
        if (variant.StartsWith("Withdraw")) withdrawn = await f.BufferAsync(await f.OtherEmployeeAsync());
        var before = await f.DigestAsync();
        var failure = variant.Contains("Failure");
        if (failure) fault.Arm("INSERT INTO \"Registrations\"");
        TrainingOperationResult result;
        if (variant.Contains("Maximum"))
            result = await f.Services.GetRequiredService<TrainingMomentManagementService>().ChangeCapacityAsync(f.Owner.Id,
                new(f.Moment.Id, f.Moment.Version, 2));
        else if (variant.StartsWith("Audience"))
            result = await f.Services.GetRequiredService<TrainingManagementService>().AssignAudienceAsync(f.Admin.Id,
                new(f.Training.Id, f.Training.Version, TrainingAudienceScope.SelectedDepartments, [f.Employee.DepartmentId!.Value]));
        else if (variant.StartsWith("Withdraw"))
            result = await f.Services.GetRequiredService<EmployeeEnrolmentService>().WithdrawAsync(withdrawn!.ApplicationUserId,
                withdrawn.Id, new EmployeeWithdrawInput { ExpectedVersion = withdrawn.Version });
        else
            result = await f.Services.GetRequiredService<TrainingMomentManagementService>().CreateAsync(f.Owner.Id,
                new(f.Training.Id, new(f.Moment.Date.AddDays(1), f.Moment.StartTime, f.Moment.EndTime, f.Room.Id, 1)));
        if (failure)
        {
            Assert.Equal(TrainingOperationStatus.StorageUnavailable, result.Status);
            Assert.True(fault.Triggered);
            Assert.Equal(before, await f.DigestAsync());
        }
        else
        {
            Assert.True(result.Succeeded);
            var registration = await f.ReadAsync(db => db.Registrations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ApplicationUserId == f.Employee.Id && x.Status == RegistrationStatus.Requested));
            Assert.NotNull(registration);
            Assert.Equal(variant.StartsWith("New") ? result.Id : f.Moment.Id, registration.TrainingMomentId);
            var closed = await f.ReadAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(x => x.Id == waiter.Id));
            Assert.NotNull(closed.ClosedAtUtc);
            Assert.NotEqual(waiter.Version, closed.Version);
            Assert.Equal(waiter.JoinedAtUtc, closed.JoinedAtUtc);
            Assert.Equal(waiter.Motivation, registration.Motivation);
        }
    }
}
