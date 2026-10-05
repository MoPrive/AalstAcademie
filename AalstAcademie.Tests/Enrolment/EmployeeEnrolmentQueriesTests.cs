using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Enrolment;

public class EmployeeEnrolmentQueriesTests
{
    [Theory]
    [InlineData("OnlyOwnRegistrations")]
    [InlineData("OnlyOwnBuffers")]
    [InlineData("FourExistingStatusesAndReasons")]
    [InlineData("ActiveAndClosedBuffers")]
    [InlineData("SeparatePage20EachKind")]
    [InlineData("CurrentRelatedDisplayData")]
    [InlineData("HistoryAfterRoleTargetLoss")]
    [InlineData("OtherWithdrawDetailNotFound")]
    [InlineData("BlockedHistoryDenied")]
    public async Task Own_history_survives_role_loss(string variant)
    {
        await using var f = await EnrolmentFixture.CreateAsync();
        var registration = await f.RegistrationAsync(); var buffer = await f.BufferAsync();
        var stranger = await f.OtherEmployeeAsync();
        await f.RegistrationAsync(user: stranger); var foreign = await f.BufferAsync(stranger);
        if (variant == "FourExistingStatusesAndReasons")
            foreach (var status in new[] { RegistrationStatus.Confirmed, RegistrationStatus.Refused, RegistrationStatus.Cancelled })
                await f.RegistrationAsync(status, moment: await f.OtherMomentAsync());
        if (variant is "ActiveAndClosedBuffers" or "SeparatePage20EachKind")
            for (var i = 0; i < (variant == "SeparatePage20EachKind" ? 21 : 1); i++)
            { await f.BufferAsync(closed: true); if (variant == "SeparatePage20EachKind") await f.RegistrationAsync(RegistrationStatus.Cancelled); }
        if (variant == "CurrentRelatedDisplayData") await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).Title = "Hernoemde opleiding");
        if (variant == "HistoryAfterRoleTargetLoss")
        { await f.RemoveEmployeeRoleAsync(); await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).AudienceScope = TrainingAudienceScope.Unassigned); }
        if (variant == "BlockedHistoryDenied") await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.Id)).IsBlocked = true);
        var before = await f.DigestAsync(); var queries = f.Services.GetRequiredService<EmployeeEnrolmentQueries>();
        var result = await queries.GetAsync(f.Employee.Id, variant == "SeparatePage20EachKind" ? 2 : 1, variant == "SeparatePage20EachKind" ? 2 : 1);
        Assert.Equal(variant == "BlockedHistoryDenied" ? TrainingOperationStatus.Forbidden : TrainingOperationStatus.Success, result.Status);
        if (variant is "OnlyOwnRegistrations" or "HistoryAfterRoleTargetLoss") Assert.Equal(registration.Id, Assert.Single(result.Registrations).Id);
        if (variant == "OnlyOwnBuffers") Assert.Equal(buffer.Id, Assert.Single(result.Waitlist).Id);
        if (variant == "FourExistingStatusesAndReasons") Assert.Equal(Enum.GetValues<RegistrationStatus>().Order(), result.Registrations.Select(x => x.Status).Order());
        if (variant == "ActiveAndClosedBuffers") { Assert.Single(result.Waitlist, x => x.ClosedAtUtc is null); Assert.Single(result.Waitlist, x => x.ClosedAtUtc is not null); }
        if (variant == "SeparatePage20EachKind") { Assert.Equal(2, result.Registrations.Count); Assert.Equal(2, result.Waitlist.Count); Assert.Equal(2, result.RegistrationPages); Assert.Equal(2, result.WaitlistPages); }
        if (variant == "CurrentRelatedDisplayData") Assert.All(result.Registrations, x => Assert.Equal("Hernoemde opleiding", x.Title));
        Assert.Equal(variant == "BlockedHistoryDenied" ? TrainingOperationStatus.Forbidden : TrainingOperationStatus.NotFound,
            (await queries.GetWithdrawAsync(f.Employee.Id, foreign.Id)).Status);
        Assert.Equal(before, await f.DigestAsync());
    }
}
