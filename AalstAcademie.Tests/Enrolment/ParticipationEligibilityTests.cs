using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Enrolment;

/// <summary>Actieve deelname en directAvailable komen uit echte rijen; deze eligibility-query schrijft zelf niets.</summary>
public class ParticipationEligibilityTests
{
    [Theory]
    [InlineData("AllDepartments")]
    [InlineData("SelectedMatching")]
    [InlineData("SelectedWrongDepartment")]
    [InlineData("Unassigned")]
    [InlineData("CancelledMoment")]
    [InlineData("ExactStart")]
    [InlineData("RunningMoment")]
    [InlineData("PastMoment")]
    [InlineData("RequestedOtherFutureMomentBlocks")]
    [InlineData("ConfirmedRunningMomentBlocks")]
    [InlineData("ActiveParticipationAtExactEndDoesNotBlock")]
    [InlineData("PastParticipationDoesNotBlock")]
    [InlineData("ActiveBufferBlocksAcrossMoments")]
    [InlineData("ClosedBufferDoesNotBlock")]
    [InlineData("RefusedSameMomentBlocks")]
    [InlineData("RefusedOtherMomentAllows")]
    [InlineData("CancelledPreviousAttemptAllows")]
    [InlineData("PhysicalCountsRequestedAndConfirmedOnly")]
    [InlineData("EligibleBufferReservesCapacity")]
    [InlineData("ClosedBufferReservesNothing")]
    [InlineData("WrongTargetBufferReservesNothing")]
    [InlineData("BlockedBufferReservesNothing")]
    [InlineData("RoleLostBufferReservesNothing")]
    [InlineData("DemoBoundaryBufferReservesNothing")]
    [InlineData("BufferWithOtherActiveParticipationReservesNothing")]
    [InlineData("BufferRefusedThisMomentReservesNothing")]
    [InlineData("RequiredMotivationMissingBufferReservesNothing")]
    [InlineData("InvalidMotivationBufferReservesNothing")]
    [InlineData("OwnBufferNotExcludedByItselfFromE")]
    [InlineData("SurplusBeyondEIsDirect")]
    [InlineData("EAbovePhysicalGivesZero")]
    [InlineData("SameTrainingDifferentMomentERecomputed")]
    [InlineData("JoinedThenIdOrdering")]
    [InlineData("ChosenFullMomentWithOtherFreeMomentAllowsBuffer")]
    [InlineData("NoFutureMomentCannotJoin")]
    public async Task Provider_rules(string variant)
    {
        await using var f = await EnrolmentFixture.CreateAsync(maximum: variant == "EAbovePhysicalGivesZero" ? 1 : 4,
            demoEnabled: variant != "DemoBoundaryBufferReservesNothing");
        var eligibility = f.Services.GetRequiredService<ParticipationEligibility>();
        var reader = f.Services.GetRequiredService<EmployeeAccessReader>();
        var schedule = f.Services.GetRequiredService<TrainingSchedule>();
        var chosenId = f.Moment.Id;
        var expectedE = 0;
        var expectedOccupied = 0;

        if (variant is "SelectedMatching" or "SelectedWrongDepartment" or "Unassigned" or "WrongTargetBufferReservesNothing")
            await f.MutateAsync(async db =>
            {
                var training = await db.Trainings.SingleAsync(x => x.Id == f.Training.Id);
                training.AudienceScope = variant == "Unassigned" ? TrainingAudienceScope.Unassigned : TrainingAudienceScope.SelectedDepartments;
                if (variant != "Unassigned")
                {
                    var department = variant == "SelectedWrongDepartment"
                        ? await db.Departments.Where(x => x.Id != f.Employee.DepartmentId).Select(x => x.Id).FirstAsync()
                        : f.Employee.DepartmentId!.Value;
                    db.TrainingDepartments.Add(new TrainingDepartment { TrainingId = training.Id, DepartmentId = department });
                }
            });
        if (variant is "CancelledMoment" or "NoFutureMomentCannotJoin")
            await f.MutateAsync(async db => (await db.TrainingMoments.SingleAsync(x => x.Id == chosenId)).Status = TrainingMomentStatus.Cancelled);
        var endpoints = schedule.Validate(f.Moment.Date, f.Moment.StartTime, f.Moment.EndTime).Value!;
        if (variant == "ExactStart") f.Clock.UtcNow = endpoints.StartUtc;
        if (variant == "RunningMoment") f.Clock.UtcNow = endpoints.StartUtc.AddMinutes(1);
        if (variant == "PastMoment") f.Clock.UtcNow = endpoints.EndUtc.AddMinutes(1);

        if (variant == "RequestedOtherFutureMomentBlocks") await f.RegistrationAsync(moment: await f.OtherMomentAsync());
        if (variant is "ConfirmedRunningMomentBlocks" or "ActiveParticipationAtExactEndDoesNotBlock" or "PastParticipationDoesNotBlock")
        {
            await f.RegistrationAsync(RegistrationStatus.Confirmed);
            f.Clock.UtcNow = variant == "ConfirmedRunningMomentBlocks" ? endpoints.StartUtc.AddMinutes(1)
                : variant == "ActiveParticipationAtExactEndDoesNotBlock" ? endpoints.EndUtc : endpoints.EndUtc.AddMinutes(1);
        }
        if (variant is "ActiveBufferBlocksAcrossMoments" or "ClosedBufferDoesNotBlock")
            await f.BufferAsync(closed: variant == "ClosedBufferDoesNotBlock");
        if (variant == "RefusedSameMomentBlocks") await f.RegistrationAsync(RegistrationStatus.Refused);
        if (variant == "RefusedOtherMomentAllows") await f.RegistrationAsync(RegistrationStatus.Refused, moment: await f.OtherMomentAsync());
        if (variant == "CancelledPreviousAttemptAllows") await f.RegistrationAsync(RegistrationStatus.Cancelled);
        if (variant == "PhysicalCountsRequestedAndConfirmedOnly")
        {
            foreach (var status in Enum.GetValues<RegistrationStatus>())
                await f.RegistrationAsync(status, await f.OtherEmployeeAsync());
            await f.RegistrationAsync(user: await f.OtherEmployeeAsync(), moment: await f.OtherMomentAsync());
            await f.BufferAsync(await f.OtherEmployeeAsync());
            expectedOccupied = 2; expectedE = 1;
        }
        // Sprint 005: maak voor deze twee read-only scenario's het tweede moment vóór de buffer.
        // Normale momentcreate verwerkt nu bestaande buffers; dat is hier niet de actie onder test.
        TrainingMoment? preparedOther = null;
        if (variant is "BufferWithOtherActiveParticipationReservesNothing" or "SameTrainingDifferentMomentERecomputed")
            preparedOther = await f.OtherMomentAsync();
        var priority = variant.Contains("Buffer", StringComparison.Ordinal) && variant is not
            ("ActiveBufferBlocksAcrossMoments" or "ClosedBufferDoesNotBlock" or "ChosenFullMomentWithOtherFreeMomentAllowsBuffer")
            || variant is "SurplusBeyondEIsDirect" or "EAbovePhysicalGivesZero" or "SameTrainingDifferentMomentERecomputed" or "JoinedThenIdOrdering";
        if (priority)
        {
            var waiter = variant == "OwnBufferNotExcludedByItselfFromE" ? f.Employee : await f.OtherEmployeeAsync();
            var entry = await f.BufferAsync(waiter, variant == "ClosedBufferReservesNothing");
            expectedE = variant is "EligibleBufferReservesCapacity" or "OwnBufferNotExcludedByItselfFromE" or
                "SurplusBeyondEIsDirect" or "EAbovePhysicalGivesZero" or "JoinedThenIdOrdering" ? 1 : 0;
            if (variant == "RoleLostBufferReservesNothing") await f.RemoveEmployeeRoleAsync(waiter.Id);
            if (variant is "WrongTargetBufferReservesNothing" or "BlockedBufferReservesNothing" or "DemoBoundaryBufferReservesNothing")
                await f.MutateAsync(async db =>
                {
                    var user = await db.Users.SingleAsync(x => x.Id == waiter.Id);
                    if (variant == "WrongTargetBufferReservesNothing") user.DepartmentId = await db.Departments.Where(x => x.Id != f.Employee.DepartmentId).Select(x => x.Id).FirstAsync();
                    if (variant == "BlockedBufferReservesNothing") user.IsBlocked = true;
                    if (variant == "DemoBoundaryBufferReservesNothing") user.DemoSeedKey = "isolated.demo";
                });
            if (variant == "BufferWithOtherActiveParticipationReservesNothing") await f.RegistrationAsync(user: waiter, moment: preparedOther!);
            if (variant is "BufferRefusedThisMomentReservesNothing" or "SameTrainingDifferentMomentERecomputed") await f.RegistrationAsync(RegistrationStatus.Refused, waiter);
            if (variant is "RequiredMotivationMissingBufferReservesNothing" or "InvalidMotivationBufferReservesNothing")
                await f.MutateAsync(async db =>
                {
                    (await db.Trainings.SingleAsync(x => x.Id == f.Training.Id)).RequiresMotivation = true;
                    // SQLite counts Unicode codepoints; a valid physical row can exceed the server's 4000 UTF-16-unit input limit.
                    (await db.WaitlistEntries.SingleAsync(x => x.Id == entry.Id)).Motivation = variant == "RequiredMotivationMissingBufferReservesNothing"
                        ? null : string.Concat(Enumerable.Repeat("😀", 2001));
                });
            if (variant is "EAbovePhysicalGivesZero" or "JoinedThenIdOrdering")
            { await f.BufferAsync(await f.OtherEmployeeAsync()); expectedE = 2; }
        }
        if (variant == "ChosenFullMomentWithOtherFreeMomentAllowsBuffer")
        {
            for (var i = 0; i < f.Moment.MaximumParticipants; i++) await f.RegistrationAsync(user: await f.OtherEmployeeAsync());
            expectedOccupied = f.Moment.MaximumParticipants;
            _ = await f.OtherMomentAsync();
        }
        var before = await f.DigestAsync();
        var snapshot = await eligibility.ReadAsync([f.Training.Id]);
        var moment = await f.Context.TrainingMoments.AsNoTracking().SingleAsync(x => x.Id == chosenId);
        var training = snapshot.Trainings[f.Training.Id];
        var actor = (await reader.ReadEmployeeAsync(f.Employee.Id))!;
        if (variant is "AllDepartments" or "SelectedMatching" or "SelectedWrongDepartment" or "Unassigned")
            Assert.Equal(variant is "AllDepartments" or "SelectedMatching", eligibility.Allows(actor.DepartmentId!.Value, training));
        if (variant is "CancelledMoment" or "ExactStart" or "RunningMoment" or "PastMoment" or "NoFutureMomentCannotJoin")
            Assert.False(eligibility.IsNewMoment(moment));
        if (variant is "RequestedOtherFutureMomentBlocks" or "ConfirmedRunningMomentBlocks" or
            "ActiveParticipationAtExactEndDoesNotBlock" or "PastParticipationDoesNotBlock" or "ActiveBufferBlocksAcrossMoments" or
            "ClosedBufferDoesNotBlock" or "RefusedSameMomentBlocks" or "RefusedOtherMomentAllows" or "CancelledPreviousAttemptAllows")
            Assert.Equal(variant is "RequestedOtherFutureMomentBlocks" or "ConfirmedRunningMomentBlocks" or "ActiveBufferBlocksAcrossMoments",
                eligibility.HasActiveParticipation(f.Employee.Id, training.Id, snapshot));
        if (variant is "RefusedSameMomentBlocks" or "RefusedOtherMomentAllows")
            Assert.Equal(variant == "RefusedSameMomentBlocks", eligibility.WasRefused(f.Employee.Id, chosenId, snapshot));
        if (priority || variant is "PhysicalCountsRequestedAndConfirmedOnly" or "ChosenFullMomentWithOtherFreeMomentAllowsBuffer")
        {
            var value = eligibility.Availability(moment, snapshot);
            Assert.Equal(Math.Max(0, moment.MaximumParticipants - expectedOccupied), value.PhysicalAvailable);
            Assert.Equal(expectedE, value.EligibleWaiters);
            Assert.Equal(Math.Max(0, value.PhysicalAvailable - expectedE), value.DirectAvailable);
        }
        if (variant == "SameTrainingDifferentMomentERecomputed")
        {
            var other = preparedOther!;
            var otherState = await eligibility.ReadAsync([training.Id]);
            Assert.Equal(1, eligibility.Availability(other, otherState).EligibleWaiters);
            before = await f.DigestAsync();
        }
        if (variant == "JoinedThenIdOrdering")
            Assert.Equal(snapshot.Waitlist.OrderBy(x => x.JoinedAtUtc).ThenBy(x => x.Id).Select(x => x.Id), snapshot.Waitlist.Select(x => x.Id));
        Assert.Equal(before, await f.DigestAsync());
    }
}
