using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Enrolment;

public sealed class TrainingMomentCancellationServiceTests
{
    [Theory]
    [InlineData("AdminCancelsFutureScheduled")][InlineData("InstructorDenied")][InlineData("StaleMomentVersion")]
    [InlineData("RequestedAndConfirmedCancelled")][InlineData("ExistingActiveBufferExact")]
    [InlineData("LegacyOtherActiveRegistrationConflict")][InlineData("NoRefillOtherMoment")]
    [InlineData("FaultAfterMomentWriteRollback")][InlineData("FaultAfterFirstRegistrationRollback")]
    [InlineData("FaultAfterFirstReturnRollback")][InlineData("ClockBeforeFinishRollback")]
    public async Task Cases(string variant)
    {
        var fault = new ParticipationWorkflowFailureInterceptor(); await using var f = await ParticipationWorkflowFixture.CreateAsync(2, fault);
        var row = await f.RegistrationAsync(); var second = await f.RegistrationAsync(RegistrationStatus.Confirmed, await f.OtherEmployeeAsync());
        WaitlistEntry? buffer = null;
        if (variant == "ExistingActiveBufferExact") buffer = await f.BufferAsync();
        TrainingMoment? other = null;
        if (variant is "LegacyOtherActiveRegistrationConflict" or "NoRefillOtherMoment") other = await f.OtherMomentAsync();
        if (variant == "LegacyOtherActiveRegistrationConflict") await f.RegistrationAsync(moment: other);
        if (variant.Contains("Fault")) fault.Arm(variant == "FaultAfterMomentWriteRollback" ? "UPDATE \"TrainingMoments\"" :
            variant == "FaultAfterFirstRegistrationRollback" ? "UPDATE \"Registrations\"" : "INSERT INTO \"WaitlistEntries\"");
        if (variant == "ClockBeforeFinishRollback") fault.ArmAction("INSERT INTO \"WaitlistEntries\"", () => f.Clock.UtcNow =
            f.Services.GetRequiredService<TrainingSchedule>().Validate(f.Moment.Date, f.Moment.StartTime, f.Moment.EndTime).Value!.StartUtc);
        var before = await f.DigestAsync();
        var result = await f.ExecuteAsync(sp => sp.GetRequiredService<TrainingMomentCancellationService>().CancelAsync(
            variant == "InstructorDenied" ? f.Owner.Id : f.Admin.Id, f.Moment.Id,
            new() { ExpectedMomentVersion = variant == "StaleMomentVersion" ? Guid.NewGuid() : f.Moment.Version }));
        Assert.Equal(variant.Contains("Fault") ? TrainingOperationStatus.StorageUnavailable : variant == "InstructorDenied" ? TrainingOperationStatus.Forbidden :
            variant is "StaleMomentVersion" or "LegacyOtherActiveRegistrationConflict" or "ClockBeforeFinishRollback" ? TrainingOperationStatus.Conflict : TrainingOperationStatus.Success, result.Status);
        if (!result.Succeeded) { Assert.Equal(before, await f.DigestAsync()); if (variant.Contains("Fault")) Assert.True(fault.Triggered); return; }
        Assert.Equal(TrainingMomentStatus.Cancelled, await f.ReadAsync(db => db.TrainingMoments.Where(x => x.Id == f.Moment.Id).Select(x => x.Status).SingleAsync()));
        foreach (var original in new[] { row, second })
        {
            var saved = await f.ReadAsync(db => db.Registrations.AsNoTracking().SingleAsync(x => x.Id == original.Id));
            Assert.Equal(RegistrationStatus.Cancelled, saved.Status); Assert.Equal(original.RequestedAtUtc, saved.RequestedAtUtc); Assert.Equal(original.Motivation, saved.Motivation);
            var returned = await f.ReadAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(x => x.ApplicationUserId == original.ApplicationUserId && x.ClosedAtUtc == null));
            Assert.Equal(buffer?.ApplicationUserId == original.ApplicationUserId ? buffer.JoinedAtUtc : original.RequestedAtUtc, returned.JoinedAtUtc);
            Assert.Equal(buffer?.ApplicationUserId == original.ApplicationUserId ? buffer.Motivation : original.Motivation, returned.Motivation);
            if (buffer?.ApplicationUserId == original.ApplicationUserId) Assert.Equal(buffer.Version, returned.Version);
        }
        if (other is not null) Assert.Equal(0, await f.ReadAsync(db => db.Registrations.CountAsync(x => x.TrainingMomentId == other.Id)));
    }
}
