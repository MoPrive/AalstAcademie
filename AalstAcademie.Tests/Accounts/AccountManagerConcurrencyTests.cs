using AalstAcademie.Tests.Enrolment;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Accounts;

/// <summary>Managerkeuze en registratie herlezen de actuele headkoppeling na de echte writer-lock.</summary>
[Collection("Revision02 provider races")]
public class AccountManagerConcurrencyTests
{
    [Theory]
    [InlineData("TwoOwnManagerPostsSameStamp")]
    [InlineData("HeadUnlinkedFirstThenOwnManager")]
    [InlineData("OwnManagerFirstThenHeadUnlinkedStoredFkRemains")]
    [InlineData("HeadUnlinkedFirstThenRegistration")]
    [InlineData("RegistrationFirstThenHeadUnlinkedStoredFkRemains")]
    [InlineData("SecurityStampRolesStatusUnaffectedByRace")]
    public async Task Current_reference_and_original_stamp(string variant)
    {
        await using var f = await EnrolmentRaceFixture.CreateAsync();
        await f.Read(async db => { (await db.Departments.SingleAsync(x => x.Id == f.Employee.DepartmentId)).ResponsibleUserId = f.Owner.Id; await db.SaveChangesAsync(); });
        ApplicationUser original = null!; int users = 0; string[] roles = [];
        await f.Read(async db => { original = await db.Users.AsNoTracking().SingleAsync(x => x.Id == f.Employee.Id); users = await db.Users.CountAsync(); roles = await db.UserRoles.Where(x => x.UserId == f.Employee.Id).Select(x => x.RoleId).OrderBy(x => x).ToArrayAsync(); });
        Task<TrainingOperationResult> Manager(IServiceProvider sp) => sp.GetRequiredService<AccountManagerService>().ChangeAsync(f.Employee.Id,
            new() { ManagerUserId = f.Owner.Id, ExpectedConcurrencyStamp = original.ConcurrencyStamp });
        Task<TrainingOperationResult> Unlink(IServiceProvider sp) => sp.GetRequiredService<DepartmentResponsibleService>().AssignAsync(f.Admin.Id, f.Employee.DepartmentId!.Value,
            new() { ResponsibleUserId = null, ExpectedResponsibleUserId = f.Owner.Id });
        AccountOperationResult? registration = null;
        async Task<TrainingOperationResult> Register(IServiceProvider sp)
        {
            // De tijdelijke invoer verlaat deze fixture niet; de readback controleert alleen servervelden.
            var password = AccountWebApplicationFactory.NewPassword();
            registration = await sp.GetRequiredService<AccountRegistrationService>().RegisterAsync(new()
            { RequestedAccountType = RequestedAccountType.Employee, Email = "manager-race@example.invalid", PhoneNumber = "0490000000",
                FirstName = "Mila", LastName = "Race", DepartmentId = f.Employee.DepartmentId, ManagerUserId = f.Owner.Id,
                Password = password, ConfirmPassword = password });
            return new(registration.Status switch { AccountOperationStatus.Success => TrainingOperationStatus.Success,
                AccountOperationStatus.ValidationFailure => TrainingOperationStatus.Invalid,
                AccountOperationStatus.Conflict => TrainingOperationStatus.Conflict, _ => TrainingOperationStatus.StorageUnavailable });
        }
        var registrationCase = variant.Contains("Registration", StringComparison.Ordinal);
        var unlinkFirst = variant.StartsWith("HeadUnlinkedFirst", StringComparison.Ordinal);
        var duplicate = variant is "TwoOwnManagerPostsSameStamp" or "SecurityStampRolesStatusUnaffectedByRace";
        TrainingOperationResult[] result = duplicate
            ? await f.Race(Manager, sp => sp.GetRequiredService<AccountManagerService>().ChangeAsync(f.Employee.Id,
                new() { ManagerUserId = null, ExpectedConcurrencyStamp = original.ConcurrencyStamp }))
            : unlinkFirst ? await f.Race(Unlink, registrationCase ? Register : Manager)
            : await f.Race(registrationCase ? Register : Manager, Unlink);
        Assert.True(result[0].Succeeded);
        Assert.Equal(duplicate ? TrainingOperationStatus.Conflict : unlinkFirst ? TrainingOperationStatus.Invalid : TrainingOperationStatus.Success, result[1].Status);
        await f.Read(async db =>
        {
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == f.Employee.Id);
            var ownChanged = !registrationCase && !unlinkFirst;
            Assert.Equal(ownChanged ? f.Owner.Id : null, user.ManagerUserId);
            Assert.True(ownChanged ? original.ConcurrencyStamp != user.ConcurrencyStamp : original.ConcurrencyStamp == user.ConcurrencyStamp);
            Assert.True(original.SecurityStamp == user.SecurityStamp);
            Assert.Equal(original.AccountApprovalStatus, user.AccountApprovalStatus); Assert.Equal(original.DepartmentId, user.DepartmentId);
            Assert.Equal(roles, await db.UserRoles.Where(x => x.UserId == user.Id).Select(x => x.RoleId).OrderBy(x => x).ToArrayAsync());
            Assert.Equal(duplicate ? f.Owner.Id : null, (await db.Departments.SingleAsync(x => x.Id == f.Employee.DepartmentId)).ResponsibleUserId);
            Assert.Equal(users + (registrationCase && !unlinkFirst ? 1 : 0), await db.Users.CountAsync());
            if (registrationCase && !unlinkFirst)
            {
                var created = await db.Users.AsNoTracking().SingleAsync(x => x.Id == registration!.UserId);
                Assert.Equal(f.Owner.Id, created.ManagerUserId); Assert.Equal(AccountApprovalStatus.Pending, created.AccountApprovalStatus);
                Assert.Equal(0, await db.UserRoles.CountAsync(x => x.UserId == created.Id));
            }
        });
    }
}
