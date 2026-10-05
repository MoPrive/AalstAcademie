using System.Diagnostics;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Enrolment;

/// <summary>Iedere variant controleert echte providerwrites of hun afwezigheid via een andere context.</summary>
public class EmployeeEnrolmentServiceTests
{
    [Theory]
    [InlineData("RequestedServerActorUtcGuid")]
    [InlineData("FullMomentRequestConflict")]
    [InlineData("PriorityReservedRequestConflict")]
    [InlineData("SurplusDirectRequestSucceeds")]
    [InlineData("SequentialDuplicateRequestConflict")]
    [InlineData("SameTrainingOtherMomentConflict")]
    [InlineData("ExistingBufferRequestConflict")]
    [InlineData("RefusedSameMomentConflict")]
    [InlineData("RefusedOtherMomentNewRow")]
    [InlineData("CancelledPreviousAttemptNewRow")]
    [InlineData("OptionalMotivationNull")]
    [InlineData("RequiredMotivationAbsentNoWrite")]
    [InlineData("OversizedMotivationNoWrite")]
    [InlineData("NulMotivationNoWrite")]
    [InlineData("StaleTrainingVersion")]
    [InlineData("StaleMomentVersion")]
    [InlineData("TimeRecheckedBeforeInsert")]
    [InlineData("RoleLostAfterPriorRead")]
    [InlineData("TargetLostAfterPriorRead")]
    [InlineData("ExplicitJoinServerFieldsNoRegistration")]
    [InlineData("JoinWithoutConsentNoWrite")]
    [InlineData("JoinWhileDirectPlaceAvailableConflict")]
    [InlineData("JoinChosenFullWithOtherFreeMoment")]
    [InlineData("DuplicateJoinPreservesOldRow")]
    [InlineData("JoinVersusExistingParticipationConflict")]
    [InlineData("JoinStartedMomentConflict")]
    [InlineData("WithdrawClosesConcreteOwnRow")]
    [InlineData("WithdrawOtherUserNotFound")]
    [InlineData("WithdrawUnknownNotFound")]
    [InlineData("WithdrawClosedConflict")]
    [InlineData("WithdrawStaleConflict")]
    [InlineData("OldWithdrawCannotCloseRejoinedRow")]
    [InlineData("WithdrawAfterRoleTargetLoss")]
    [InlineData("WithdrawBlockedForbidden")]
    [InlineData("RequestSqlFailureRollback")]
    [InlineData("JoinSqlFailureRollback")]
    [InlineData("WithdrawSqlFailureRollback")]
    [InlineData("BoundedBusyStorageUnavailable")]
    public async Task Atomic_participation(string variant)
    {
        var fault = new EnrolmentFailureInterceptor(); var stepping = new AdvancingClock();
        await using var f = await EnrolmentFixture.CreateAsync(maximum: variant == "PriorityReservedRequestConflict" ? 1 : 2,
            interceptor: fault, clock: variant == "TimeRecheckedBeforeInsert" ? stepping : null);
        var join = variant.StartsWith("Join", StringComparison.Ordinal) || variant is "ExplicitJoinServerFieldsNoRegistration" or "DuplicateJoinPreservesOldRow";
        var withdraw = variant.StartsWith("Withdraw", StringComparison.Ordinal) || variant == "OldWithdrawCannotCloseRejoinedRow";
        var expected = TrainingOperationStatus.Success;
        EmployeeRequestInput input = f.Request("  geldige motivatie  ");
        WaitlistEntry? entry = null;
        if (join && variant != "JoinWhileDirectPlaceAvailableConflict" || variant == "FullMomentRequestConflict")
            for (var i = 0; i < f.Moment.MaximumParticipants; i++) await f.RegistrationAsync(user: await f.OtherEmployeeAsync());
        if (variant is "PriorityReservedRequestConflict" or "SurplusDirectRequestSucceeds") await f.BufferAsync(await f.OtherEmployeeAsync());
        if (variant == "SequentialDuplicateRequestConflict") Assert.True((await f.Enrolments.RequestAsync(f.Employee.Id, f.Moment.Id, input)).Succeeded);
        if (variant == "SameTrainingOtherMomentConflict") await f.RegistrationAsync(moment: await f.OtherMomentAsync());
        if (variant == "ExistingBufferRequestConflict") await f.BufferAsync();
        if (variant == "RefusedSameMomentConflict") await f.RegistrationAsync(RegistrationStatus.Refused);
        if (variant == "RefusedOtherMomentNewRow") await f.RegistrationAsync(RegistrationStatus.Refused, moment: await f.OtherMomentAsync());
        if (variant == "CancelledPreviousAttemptNewRow") await f.RegistrationAsync(RegistrationStatus.Cancelled);
        if (variant == "OptionalMotivationNull") input = input with { Motivation = "   " };
        if (variant == "RequiredMotivationAbsentNoWrite")
        { await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).RequiresMotivation = true); input = input with { Motivation = null }; }
        if (variant == "OversizedMotivationNoWrite") input = input with { Motivation = new string('a', 4001) };
        if (variant == "NulMotivationNoWrite") input = input with { Motivation = "mot\0ivatie" };
        if (variant == "StaleTrainingVersion") input = input with { ExpectedTrainingVersion = Guid.NewGuid() };
        if (variant == "StaleMomentVersion") input = input with { ExpectedMomentVersion = Guid.NewGuid() };
        if (variant == "TimeRecheckedBeforeInsert") stepping.Arm(f.Services.GetRequiredService<TrainingSchedule>().Validate(f.Moment.Date, f.Moment.StartTime, f.Moment.EndTime).Value!.StartUtc);
        if (variant is "RoleLostAfterPriorRead" or "TargetLostAfterPriorRead")
        {
            Assert.NotNull((await f.Services.GetRequiredService<EmployeeCatalogueQueries>().GetMomentAsync(f.Employee.Id, f.Moment.Id)).Moment);
            if (variant == "RoleLostAfterPriorRead") await f.RemoveEmployeeRoleAsync();
            else await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).AudienceScope = TrainingAudienceScope.Unassigned);
        }
        if (variant == "JoinChosenFullWithOtherFreeMoment") _ = await f.OtherMomentAsync();
        if (variant == "DuplicateJoinPreservesOldRow") entry = await f.BufferAsync();
        if (variant == "JoinVersusExistingParticipationConflict") await f.RegistrationAsync(moment: await f.OtherMomentAsync());
        if (variant == "JoinStartedMomentConflict") f.Clock.UtcNow = f.Services.GetRequiredService<TrainingSchedule>().Validate(f.Moment.Date, f.Moment.StartTime, f.Moment.EndTime).Value!.StartUtc;
        if (withdraw)
        {
            entry = await f.BufferAsync(variant == "WithdrawOtherUserNotFound" ? await f.OtherEmployeeAsync() : null,
                variant is "WithdrawClosedConflict" or "OldWithdrawCannotCloseRejoinedRow");
            if (variant == "OldWithdrawCannotCloseRejoinedRow") _ = await f.BufferAsync();
            if (variant == "WithdrawAfterRoleTargetLoss") { await f.RemoveEmployeeRoleAsync(); await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).AudienceScope = TrainingAudienceScope.Unassigned); }
            if (variant == "WithdrawBlockedForbidden") await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.Id)).IsBlocked = true);
        }
        if (variant is "FullMomentRequestConflict" or "PriorityReservedRequestConflict" or "SequentialDuplicateRequestConflict" or
            "SameTrainingOtherMomentConflict" or "ExistingBufferRequestConflict" or "RefusedSameMomentConflict" or
            "StaleTrainingVersion" or "StaleMomentVersion" or "TimeRecheckedBeforeInsert" or "JoinWhileDirectPlaceAvailableConflict" or
            "DuplicateJoinPreservesOldRow" or "JoinVersusExistingParticipationConflict" or "JoinStartedMomentConflict" or
            "WithdrawClosedConflict" or "WithdrawStaleConflict" or "OldWithdrawCannotCloseRejoinedRow") expected = TrainingOperationStatus.Conflict;
        if (variant is "RequiredMotivationAbsentNoWrite" or "OversizedMotivationNoWrite" or "NulMotivationNoWrite" or "JoinWithoutConsentNoWrite") expected = TrainingOperationStatus.Invalid;
        if (variant is "RoleLostAfterPriorRead" or "TargetLostAfterPriorRead" or "WithdrawBlockedForbidden") expected = TrainingOperationStatus.Forbidden;
        if (variant is "WithdrawOtherUserNotFound" or "WithdrawUnknownNotFound") expected = TrainingOperationStatus.NotFound;
        if (variant.EndsWith("SqlFailureRollback", StringComparison.Ordinal)) { fault.Arm(withdraw ? "withdraw" : join ? "join" : "request"); expected = TrainingOperationStatus.StorageUnavailable; }
        var before = await f.DigestAsync();
        TrainingOperationResult result;
        if (variant == "BoundedBusyStorageUnavailable")
        {
            using var holder = new SqliteConnection(f.Database.ConnectionString); await holder.OpenAsync();
            using var reservation = holder.BeginTransaction(deferred: false); var timer = Stopwatch.StartNew();
            result = await Task.Run(async () => { await using var scope = f.Database.CreateScope(); return await scope.ServiceProvider.GetRequiredService<EmployeeEnrolmentService>().RequestAsync(f.Employee.Id, f.Moment.Id, input); });
            Assert.InRange(timer.Elapsed.TotalSeconds, 4, 15); expected = TrainingOperationStatus.StorageUnavailable;
        }
        else if (withdraw) result = await f.Enrolments.WithdrawAsync(f.Employee.Id, variant == "WithdrawUnknownNotFound" ? -1 : entry!.Id,
            new() { ExpectedVersion = variant == "WithdrawStaleConflict" ? Guid.NewGuid() : entry!.Version });
        else if (join) result = await f.Enrolments.JoinAsync(f.Employee.Id, f.Moment.Id, f.Join(input.Motivation, variant != "JoinWithoutConsentNoWrite"));
        else result = await f.Enrolments.RequestAsync(f.Employee.Id, f.Moment.Id, input);
        Assert.Equal(expected, result.Status);
        if (!result.Succeeded) Assert.Equal(before, await f.DigestAsync());
        else
        {
            await using var readback = f.Database.CreateScope(); var db = readback.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (withdraw) { var row = await db.WaitlistEntries.SingleAsync(x => x.Id == entry!.Id); Assert.NotNull(row.ClosedAtUtc); Assert.NotEqual(entry!.Version, row.Version); Assert.Equal(entry.JoinedAtUtc, row.JoinedAtUtc); }
            else if (join) { var row = await db.WaitlistEntries.SingleAsync(x => x.Id == result.Id); Assert.Equal(f.Employee.Id, row.ApplicationUserId); Assert.Null(row.ClosedAtUtc); Assert.NotEqual(Guid.Empty, row.Version); Assert.Empty(await db.Registrations.Where(x => x.ApplicationUserId == f.Employee.Id).ToListAsync()); }
            else { var row = await db.Registrations.SingleAsync(x => x.Id == result.Id); Assert.Equal(RegistrationStatus.Requested, row.Status); Assert.Equal(f.Employee.Id, row.ApplicationUserId); Assert.Equal(f.Clock.GetUtcNow().UtcDateTime, row.RequestedAtUtc); Assert.NotEqual(Guid.Empty, row.Version); Assert.Equal(variant == "OptionalMotivationNull" ? null : "geldige motivatie", row.Motivation); }
        }
        if (variant.EndsWith("SqlFailureRollback", StringComparison.Ordinal)) Assert.True(fault.Triggered);
        Assert.Equal(0, f.Services.GetRequiredService<RecordingAccountApplicationsNotifier>().Count);
    }

    // Tijd verandert na de eerste guardlezingen, zonder op de hostklok of Thread.Sleep te vertrouwen.
    private sealed class AdvancingClock : TimeProvider
    {
        private DateTimeOffset? start; private int reads;
        public void Arm(DateTimeOffset value) { start = value; reads = 0; }
        public override DateTimeOffset GetUtcNow() => start is not null && ++reads >= 3 ? start.Value : new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    }
}
