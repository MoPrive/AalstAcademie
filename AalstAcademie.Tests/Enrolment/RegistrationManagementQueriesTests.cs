using System.Text.Json;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Enrolment;

public sealed class RegistrationManagementQueriesTests
{
    [Theory]
    [InlineData("AdminAll")][InlineData("OwnerOwn")][InlineData("ForeignOwnerHidden")]
    [InlineData("EmployeeDenied")][InlineData("AnonymousDenied")][InlineData("PendingDenied")]
    [InlineData("BlockedDenied")][InlineData("DemoOutsideModeDenied")][InlineData("RoleRevokedAfterRead")]
    [InlineData("TrainingFilter")][InlineData("MomentFilter")][InlineData("StatusFilter")]
    [InlineData("SeparateBufferNoReview")][InlineData("Page20")][InlineData("StableOrder")]
    [InlineData("PageClamped")][InlineData("CurrentContactDepartmentManager")][InlineData("ManagerNoneReadable")]
    [InlineData("ReadDoesNotMutate")][InlineData("NoSecretsProjection")]
    public async Task Cases(string variant)
    {
        await using var f = await ParticipationWorkflowFixture.CreateAsync(demoEnabled: variant != "DemoOutsideModeDenied");
        var row = await f.RegistrationAsync(); WaitlistEntry? entry = variant == "MomentFilter" ? null : await f.BufferAsync(await f.OtherEmployeeAsync());
        string? actor = variant == "AdminAll" ? f.Admin.Id : f.Owner.Id;
        if (variant == "EmployeeDenied") actor = f.Employee.Id;
        if (variant == "AnonymousDenied") actor = null;
        if (variant == "ForeignOwnerHidden") actor = (await TrainingTestData.CreateOwnerAsync(f.Services)).Id;
        if (variant is "PendingDenied" or "BlockedDenied" or "DemoOutsideModeDenied")
            await f.MutateAsync(async db => { var u = await db.Users.SingleAsync(x => x.Id == f.Owner.Id);
                if (variant == "PendingDenied") u.AccountApprovalStatus = AccountApprovalStatus.Pending;
                if (variant == "BlockedDenied") u.IsBlocked = true;
                if (variant == "DemoOutsideModeDenied") u.DemoSeedKey = "fictieve-demo"; });
        if (variant == "RoleRevokedAfterRead")
        {
            var old = await f.ExecuteAsync(sp => sp.GetRequiredService<RegistrationManagementQueries>().GetDetailsAsync(actor, row.Id));
            Assert.Equal(TrainingOperationStatus.Success, old.Status);
            await f.RemoveRoleAsync(f.Owner.Id, RoleNames.Lesgever);
        }
        if (variant == "Page20") for (var i = 0; i < 20; i++)
        { await f.RegistrationAsync(RegistrationStatus.Cancelled); await f.BufferAsync(await f.OtherEmployeeAsync()); }
        if (variant == "StatusFilter") await f.RegistrationAsync(RegistrationStatus.Refused);
        TrainingMoment? other = null;
        if (variant == "MomentFilter") { other = await f.OtherMomentAsync(); await f.RegistrationAsync(RegistrationStatus.Cancelled, moment: other); }
        entry ??= await f.BufferAsync(await f.OtherEmployeeAsync());
        if (variant == "CurrentContactDepartmentManager")
            await f.MutateAsync(async db => { var u = await db.Users.SingleAsync(x => x.Id == f.Employee.Id);
                u.FirstName = "Actueel"; u.Email = "actueel@example.invalid"; u.ManagerUserId = f.Owner.Id;
                u.DepartmentId = await db.Departments.Where(x => x.Id != u.DepartmentId).Select(x => x.Id).FirstAsync(); });
        var before = await f.DigestAsync();
        var result = await f.ExecuteAsync(sp => sp.GetRequiredService<RegistrationManagementQueries>().GetAsync(actor,
            trainingId: variant == "TrainingFilter" ? f.Training.Id : null,
            momentId: other?.Id, status: variant == "StatusFilter" ? RegistrationStatus.Refused : null,
            registrationPage: variant == "PageClamped" ? int.MaxValue : 1,
            waitlistPage: variant == "PageClamped" ? -8 : 1));
        if (variant.EndsWith("Denied") || variant == "RoleRevokedAfterRead")
        { Assert.Equal(TrainingOperationStatus.Forbidden, result.Status); Assert.Empty(result.Registrations); Assert.Empty(result.Waitlist); }
        else
        {
            Assert.Equal(TrainingOperationStatus.Success, result.Status);
            if (variant == "ForeignOwnerHidden")
            {
                Assert.Empty(result.Registrations); Assert.Empty(result.Waitlist);
                var detail = await f.ExecuteAsync(sp => sp.GetRequiredService<RegistrationManagementQueries>().GetDetailsAsync(actor, row.Id));
                Assert.Equal(TrainingOperationStatus.NotFound, detail.Status);
            }
            else if (variant == "Page20")
            {
                Assert.Equal(20, result.Registrations.Count); Assert.Equal(20, result.Waitlist.Count);
                var next = await f.ExecuteAsync(sp => sp.GetRequiredService<RegistrationManagementQueries>().GetAsync(actor, registrationPage: 2, waitlistPage: 2));
                Assert.Single(next.Registrations); Assert.Single(next.Waitlist);
            }
            else
            {
                var item = Assert.Single(result.Registrations);
                Assert.Equal(variant == "StatusFilter" ? RegistrationStatus.Refused : variant == "MomentFilter" ? RegistrationStatus.Cancelled : RegistrationStatus.Requested, item.Status);
                Assert.Equal(other?.Id ?? f.Moment.Id, item.MomentId);
                Assert.Equal(f.Training.Id, item.TrainingId);
                Assert.Single(result.Waitlist);
                if (variant == "SeparateBufferNoReview")
                { Assert.Equal(entry.Id, result.Waitlist[0].Id); Assert.DoesNotContain(result.Waitlist[0].GetType().GetProperties(), x => x.Name.StartsWith("Can")); }
                entry ??= await f.BufferAsync(await f.OtherEmployeeAsync());
        if (variant == "CurrentContactDepartmentManager")
                { Assert.StartsWith("Actueel", item.ParticipantName); Assert.Equal("actueel@example.invalid", item.Email); Assert.NotNull(item.Manager); Assert.NotNull(item.Department); }
                if (variant == "ManagerNoneReadable") Assert.Null(item.Manager);
                if (variant == "PageClamped") { Assert.Equal(1, result.RegistrationPage); Assert.Equal(1, result.WaitlistPage); }
                if (variant == "NoSecretsProjection")
                    foreach (var secret in new[] { "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "Token" })
                        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result), StringComparison.Ordinal);
                if (variant == "StableOrder")
                {
                    await f.RegistrationAsync(RegistrationStatus.Cancelled);
                    var ordered = await f.ExecuteAsync(sp => sp.GetRequiredService<RegistrationManagementQueries>().GetAsync(actor));
                    Assert.Equal(ordered.Registrations.OrderBy(x => x.Id).Select(x => x.Id), ordered.Registrations.Select(x => x.Id));
                }
            }
        }
        // Ieder GET-scenario bewijst via een andere verbinding dat het niets schrijft.
        if (variant != "StableOrder") Assert.Equal(before, await f.DigestAsync());
    }
}
