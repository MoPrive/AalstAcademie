using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Enrolment;

public sealed class WaitlistPromotionServiceTests
{
    [Theory]
    [InlineData("UsesPhysicalNotDirectCapacity")][InlineData("NeverOverfills")][InlineData("JoinedAtFifo")]
    [InlineData("IdTieBreaker")][InlineData("SkipsBlockedHeadKeepsExactRow")][InlineData("SkipsRoleLostHead")]
    [InlineData("MissingRequiredMotivationSkipped")][InlineData("RefusedOtherMomentAllowed")]
    [InlineData("OtherActiveRegistrationBlocks")][InlineData("MutationFaultAfterInsertRollback")]
    [InlineData("MutationFaultAfterClosureRollback")][InlineData("MultiMomentOneActivePerTraining")]
    public async Task Cases(string variant)
    {
        var fault = new ParticipationWorkflowFailureInterceptor(); await using var f = await ParticipationWorkflowFixture.CreateAsync(1, fault);
        var first = await f.BufferAsync(); var other = await f.OtherEmployeeAsync(); var second = await f.BufferAsync(other);
        if (variant == "JoinedAtFifo") await f.MutateAsync(async db => (await db.WaitlistEntries.SingleAsync(x => x.Id == second.Id)).JoinedAtUtc = first.JoinedAtUtc.AddDays(-1));
        if (variant == "SkipsBlockedHeadKeepsExactRow") await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.Id)).IsBlocked = true);
        if (variant == "SkipsRoleLostHead") await f.RemoveRoleAsync(f.Employee.Id, AalstAcademie.Web.Models.Identity.RoleNames.Medewerker);
        if (variant == "MissingRequiredMotivationSkipped") await f.MutateAsync(async db =>
        { (await db.Trainings.SingleAsync()).RequiresMotivation = true; (await db.WaitlistEntries.SingleAsync(x => x.Id == first.Id)).Motivation = null; });
        if (variant == "RefusedOtherMomentAllowed") await f.RegistrationAsync(RegistrationStatus.Refused);
        if (variant == "OtherActiveRegistrationBlocks") await f.RegistrationAsync();
        if (variant.Contains("Fault")) fault.Arm(variant.Contains("Insert") ? "INSERT INTO \"Registrations\"" : "UPDATE \"WaitlistEntries\"");
        var before = await f.DigestAsync();
        var created = await f.ExecuteAsync(sp => sp.GetRequiredService<TrainingMomentManagementService>().CreateAsync(f.Owner.Id,
            new(f.Training.Id, new(f.Moment.Date.AddDays(1), f.Moment.StartTime, f.Moment.EndTime, f.Room.Id, 1))));
        if (variant.Contains("Fault"))
        { Assert.Equal(TrainingOperationStatus.StorageUnavailable, created.Status); Assert.True(fault.Triggered); Assert.Equal(before, await f.DigestAsync()); return; }
        Assert.True(created.Succeeded);
        var requested = await f.ReadAsync(db => db.Registrations.AsNoTracking().SingleAsync(x => x.TrainingMomentId == created.Id));
        var skip = variant.StartsWith("Skips") || variant is "MissingRequiredMotivationSkipped" or "OtherActiveRegistrationBlocks";
        var winner = variant == "JoinedAtFifo" || skip ? second : first;
        Assert.Equal(winner.ApplicationUserId, requested.ApplicationUserId); Assert.Equal(RegistrationStatus.Requested, requested.Status);
        Assert.Equal(winner.Motivation, requested.Motivation); Assert.Equal(f.Clock.GetUtcNow().UtcDateTime, requested.RequestedAtUtc);
        Assert.NotEqual(Guid.Empty, requested.Version);
        var closed = await f.ReadAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(x => x.Id == winner.Id));
        Assert.NotNull(closed.ClosedAtUtc); Assert.NotEqual(winner.Version, closed.Version);
        Assert.Equal(winner.Motivation, closed.Motivation);
        if (skip) { var unchanged = await f.ReadAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(x => x.Id == first.Id)); Assert.Null(unchanged.ClosedAtUtc); Assert.Equal(first.Version, unchanged.Version); }
        if (variant == "MultiMomentOneActivePerTraining")
        {
            var next = await f.OtherMomentAsync(f.Moment.Date.AddDays(2));
            Assert.Equal(1, await f.ReadAsync(db => db.Registrations.CountAsync(x => x.ApplicationUserId == winner.ApplicationUserId && x.Status == RegistrationStatus.Requested)));
            Assert.Equal(1, await f.ReadAsync(db => db.Registrations.CountAsync(x => x.TrainingMomentId == next.Id)));
        }
        Assert.Equal(1, await f.ReadAsync(db => db.Registrations.CountAsync(x => x.TrainingMomentId == created.Id)));
    }
}
