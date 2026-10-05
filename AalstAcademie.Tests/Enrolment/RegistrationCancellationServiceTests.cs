using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Enrolment;

public sealed class RegistrationCancellationServiceTests
{
    [Theory]
    [InlineData("OwnRequestedCancelled")][InlineData("OwnConfirmedCancelled")][InlineData("OtherOwnerNotFound")]
    [InlineData("StaleRegistrationVersion")][InlineData("EmployeeRoleLossAllowed")][InlineData("BlockedAccountDenied")]
    [InlineData("FillsSameMomentAtomically")][InlineData("RefillSqlFaultRollback")][InlineData("ClockBeforeFinishRollback")]
    public async Task Cases(string variant)
    {
        var fault = new ParticipationWorkflowFailureInterceptor(); await using var f = await ParticipationWorkflowFixture.CreateAsync(1, fault);
        var row = await f.RegistrationAsync(variant == "OwnConfirmedCancelled" ? RegistrationStatus.Confirmed : RegistrationStatus.Requested);
        var input = new RegistrationCancelInput { ExpectedRegistrationVersion = variant == "StaleRegistrationVersion" ? Guid.NewGuid() : row.Version,
            ExpectedMomentVersion = f.Moment.Version };
        if (variant == "EmployeeRoleLossAllowed") await f.RemoveRoleAsync(f.Employee.Id, AalstAcademie.Web.Models.Identity.RoleNames.Medewerker);
        if (variant == "BlockedAccountDenied") await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.Id)).IsBlocked = true);
        WaitlistEntry? buffer = null;
        if (variant is "FillsSameMomentAtomically" or "RefillSqlFaultRollback") buffer = await f.BufferAsync(await f.OtherEmployeeAsync());
        if (variant == "RefillSqlFaultRollback") fault.Arm("INSERT INTO \"Registrations\"");
        if (variant == "ClockBeforeFinishRollback") fault.ArmAction("UPDATE \"Registrations\"", () => f.Clock.UtcNow =
            f.Services.GetRequiredService<TrainingSchedule>().Validate(f.Moment.Date, f.Moment.StartTime, f.Moment.EndTime).Value!.StartUtc);
        var before = await f.DigestAsync();
        var result = await f.ExecuteAsync(sp => sp.GetRequiredService<RegistrationCancellationService>().CancelAsync(
            variant == "OtherOwnerNotFound" ? f.Owner.Id : f.Employee.Id, row.Id, input));
        Assert.Equal(variant switch { "OtherOwnerNotFound" => TrainingOperationStatus.NotFound, "StaleRegistrationVersion" or "ClockBeforeFinishRollback" => TrainingOperationStatus.Conflict,
            "BlockedAccountDenied" => TrainingOperationStatus.Forbidden, "RefillSqlFaultRollback" => TrainingOperationStatus.StorageUnavailable, _ => TrainingOperationStatus.Success }, result.Status);
        if (!result.Succeeded) { Assert.Equal(before, await f.DigestAsync()); return; }
        var saved = await f.ReadAsync(db => db.Registrations.AsNoTracking().SingleAsync(x => x.Id == row.Id));
        Assert.Equal(RegistrationStatus.Cancelled, saved.Status); Assert.NotEqual(row.Version, saved.Version);
        Assert.Equal(row.RequestedAtUtc, saved.RequestedAtUtc); Assert.Equal(row.Motivation, saved.Motivation);
        Assert.Equal(0, await f.ReadAsync(db => db.WaitlistEntries.CountAsync(x => x.ApplicationUserId == f.Employee.Id)));
        if (buffer is not null) Assert.Equal(1, await f.ReadAsync(db => db.Registrations.CountAsync(x => x.ApplicationUserId == buffer.ApplicationUserId && x.Status == RegistrationStatus.Requested)));
    }
}
