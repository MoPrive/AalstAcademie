using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Accounts;
/// <summary>Nieuwe keuzes worden onder de writerlock gecontroleerd; een bestaande keuze wordt bij review niet herkeurd.</summary>
public class AccountManagerRegistrationTests
{
    [Theory]
    [InlineData("EmployeeNone")]
    [InlineData("EmployeeLinkedHead")]
    [InlineData("InternalInstructorNone")]
    [InlineData("InternalInstructorLinkedHead")]
    [InlineData("ExternalWithManagerRejected")]
    [InlineData("UnknownManagerRejected")]
    [InlineData("UnlinkedManagerRejected")]
    [InlineData("WhitespaceToNone")]
    [InlineData("RegistrationProfileAndPendingAtomic")]
    [InlineData("ReviewAcceptsNullAndPreviouslyValidManager")]
    [InlineData("ManagerNeverGrantsRolesOrApproval")]
    [InlineData("HeadRemovedBeforeWriteRejected")]
    public async Task Optional_current_fk(string variant)
    {
        await using var f = await EnrolmentFixture.CreateAsync(); await f.LinkHeadAsync();
        if (variant == "ReviewAcceptsNullAndPreviouslyValidManager")
        {
            foreach (var withManager in new[] { false, true })
            {
                var pending = await TestAccountData.CreateAsync(f.Services, RequestedAccountType.Employee);
                await f.MutateAsync(async db => { (await db.Users.SingleAsync(x => x.Id == pending.Id)).ManagerUserId = withManager ? f.Owner.Id : null;
                    (await db.Departments.SingleAsync(x => x.Id == pending.DepartmentId)).ResponsibleUserId = null; });
                Assert.True((await f.Services.GetRequiredService<AccountReviewService>().ReviewAsync(f.Admin.Id,
                    new(pending.Id, pending.ConcurrencyStamp!, AccountReviewAction.Approve, pending.DepartmentId))).Succeeded);
            }
            return;
        }
        var external = variant == "ExternalWithManagerRejected";
        var type = external ? RequestedAccountType.ExternalInstructor : variant.StartsWith("Internal", StringComparison.Ordinal) ||
            variant == "RegistrationProfileAndPendingAtomic" ? RequestedAccountType.InternalInstructor : RequestedAccountType.Employee;
        var managerId = variant switch { "EmployeeNone" or "InternalInstructorNone" or "RegistrationProfileAndPendingAtomic" => null,
            "WhitespaceToNone" => "  ", "UnknownManagerRejected" => "absent", "UnlinkedManagerRejected" => f.Admin.Id, _ => f.Owner.Id };
        if (variant == "HeadRemovedBeforeWriteRejected")
        { Assert.True(await f.Services.GetRequiredService<AccountManagerChoices>().IsAllowedAsync(managerId));
            await f.MutateAsync(async db => (await db.Departments.SingleAsync(x => x.Id == f.Employee.DepartmentId)).ResponsibleUserId = null); }
        var password = AccountWebApplicationFactory.NewPassword();
        var command = new AccountRegistrationCommand { RequestedAccountType = type, Email = $"new-{Guid.NewGuid():N}@example.invalid",
            PhoneNumber = "0490000000", Password = password, ConfirmPassword = password, FirstName = external ? null : "Noor",
            LastName = external ? null : "Demo", DepartmentId = external ? null : f.Employee.DepartmentId, ManagerUserId = managerId,
            OrganizationName = external ? "Fictieve organisatie" : null };
        var before = await f.DigestAsync(); var result = await f.Services.GetRequiredService<AccountRegistrationService>().RegisterAsync(command);
        if (variant is "ExternalWithManagerRejected" or "UnknownManagerRejected" or "UnlinkedManagerRejected" or "HeadRemovedBeforeWriteRejected")
        { Assert.Equal(AccountOperationStatus.ValidationFailure, result.Status); Assert.Contains("ManagerUserId", result.Errors!.Keys); Assert.Equal(before, await f.DigestAsync()); }
        else
        {
            Assert.True(result.Succeeded);
            await using var scope = f.Database.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.Users.AsNoTracking().SingleAsync(x => x.Id == result.UserId);
            Assert.Equal(AccountApprovalStatus.Pending, row.AccountApprovalStatus); Assert.False(row.EmailConfirmed);
            Assert.Equal(string.IsNullOrWhiteSpace(managerId) ? null : managerId, row.ManagerUserId);
            Assert.Empty(await db.UserRoles.Where(x => x.UserId == row.Id).ToListAsync());
            Assert.Equal(type == RequestedAccountType.InternalInstructor ? 1 : 0, await db.InternalInstructors.CountAsync(x => x.ApplicationUserId == row.Id));
            Assert.Empty(AccountProfileRules.Validate(row));
        }
    }
}
