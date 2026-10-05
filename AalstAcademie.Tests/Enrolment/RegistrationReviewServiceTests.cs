using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Enrolment;

public sealed class RegistrationReviewServiceTests
{
    [Theory]
    [InlineData("OwnerConfirmsRequested")][InlineData("AdminConfirmsRequested")]
    [InlineData("OwnerRefusesRequested")][InlineData("AdminRefusesConfirmed")]
    [InlineData("OwnerCannotRefuseConfirmed")][InlineData("ForeignOwnerNotFound")]
    [InlineData("Reason1001Rejected")][InlineData("ReasonNulRejected")]
    [InlineData("StaleRegistrationVersion")][InlineData("StaleMomentVersion")]
    [InlineData("ExactStartRejected")][InlineData("RefusedTerminalRejected")]
    [InlineData("RefuseRefillsSameMoment")][InlineData("RefuseRefillSqlFaultRollback")]
    [InlineData("RefuseClockBeforeFinishRollback")]
    public async Task Cases(string variant)
    {
        var fault = new ParticipationWorkflowFailureInterceptor();
        await using var f = await ParticipationWorkflowFixture.CreateAsync(1, fault);
        var row = await f.RegistrationAsync(variant.Contains("Confirmed") ? RegistrationStatus.Confirmed :
            variant == "RefusedTerminalRejected" ? RegistrationStatus.Refused : RegistrationStatus.Requested);
        var actor = variant.StartsWith("Admin") ? f.Admin.Id : f.Owner.Id;
        if (variant == "ForeignOwnerNotFound") actor = (await TrainingTestData.CreateOwnerAsync(f.Services)).Id;
        var refuse = variant.Contains("Refus") || variant.StartsWith("Reason");
        var input = new RegistrationRefuseInput { ExpectedRegistrationVersion = variant == "StaleRegistrationVersion" ? Guid.NewGuid() : row.Version,
            ExpectedMomentVersion = variant == "StaleMomentVersion" ? Guid.NewGuid() : f.Moment.Version,
            DecisionReason = variant == "Reason1001Rejected" ? new string('r', 1001) : variant == "ReasonNulRejected" ? "r\0" : "  Fictieve reden  " };
        WaitlistEntry? buffer = null;
        if (variant.StartsWith("Refuse")) buffer = await f.BufferAsync(await f.OtherEmployeeAsync());
        var start = f.Services.GetRequiredService<TrainingSchedule>().Validate(f.Moment.Date, f.Moment.StartTime, f.Moment.EndTime).Value!.StartUtc;
        if (variant == "ExactStartRejected") f.Clock.UtcNow = start;
        if (variant == "RefuseRefillSqlFaultRollback") fault.Arm("INSERT INTO \"Registrations\"");
        if (variant == "RefuseClockBeforeFinishRollback") fault.ArmAction("UPDATE \"Registrations\"", () => f.Clock.UtcNow = start);
        var before = await f.DigestAsync();
        var result = await f.ExecuteAsync(sp => refuse ? sp.GetRequiredService<RegistrationReviewService>().RefuseAsync(actor, row.Id, input) :
            sp.GetRequiredService<RegistrationReviewService>().ConfirmAsync(actor, row.Id, input));
        var expected = variant switch
        {
            "OwnerCannotRefuseConfirmed" => TrainingOperationStatus.Forbidden,
            "ForeignOwnerNotFound" => TrainingOperationStatus.NotFound,
            "Reason1001Rejected" or "ReasonNulRejected" => TrainingOperationStatus.Invalid,
            "StaleRegistrationVersion" or "StaleMomentVersion" or "ExactStartRejected" or "RefusedTerminalRejected" or "RefuseClockBeforeFinishRollback" => TrainingOperationStatus.Conflict,
            "RefuseRefillSqlFaultRollback" => TrainingOperationStatus.StorageUnavailable,
            _ => TrainingOperationStatus.Success
        };
        Assert.Equal(expected, result.Status);
        if (!result.Succeeded) { Assert.Equal(before, await f.DigestAsync()); if (variant.Contains("Fault") || variant.Contains("ClockBefore")) Assert.True(fault.Triggered); return; }
        var saved = await f.ReadAsync(db => db.Registrations.AsNoTracking().SingleAsync(x => x.Id == row.Id));
        Assert.Equal(refuse ? RegistrationStatus.Refused : RegistrationStatus.Confirmed, saved.Status);
        Assert.NotEqual(row.Version, saved.Version); Assert.Equal(row.RequestedAtUtc, saved.RequestedAtUtc); Assert.Equal(row.Motivation, saved.Motivation);
        Assert.Equal(refuse ? "Fictieve reden" : row.DecisionReason, saved.DecisionReason);
        if (buffer is not null)
        {
            var promoted = await f.ReadAsync(db => db.Registrations.AsNoTracking().SingleAsync(x => x.ApplicationUserId == buffer.ApplicationUserId));
            Assert.Equal(f.Moment.Id, promoted.TrainingMomentId); Assert.Equal(RegistrationStatus.Requested, promoted.Status);
            Assert.NotNull((await f.ReadAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(x => x.Id == buffer.Id))).ClosedAtUtc);
        }
        Assert.Equal(0, await f.ReadAsync(db => db.WaitlistEntries.CountAsync(x => x.ApplicationUserId == f.Employee.Id)));
    }
}
