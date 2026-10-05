using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Enrolment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Enrolment;

/// <summary>Werkelijke actuele opslag beslist; een oude EF-user of oude rolclaim verleent geen toegang.</summary>
public class EmployeeAccessReaderTests
{
    [Theory]
    [InlineData("ApprovedEmployee")]
    [InlineData("InternalInstructorWithMedewerkerOwnTraining")]
    [InlineData("InstructorWithoutMedewerker")]
    [InlineData("AdministratorWithoutMedewerker")]
    [InlineData("MissingUser")]
    [InlineData("Pending")]
    [InlineData("Refused")]
    [InlineData("Blocked")]
    [InlineData("MissingDepartment")]
    [InlineData("NonexistentDepartment")]
    [InlineData("DemoAccountOutsideDemo")]
    [InlineData("GeneralApprovedAfterRoleAndTargetLoss")]
    [InlineData("NoManagerDoesNotBlock")]
    [InlineData("TrackedOldRolesAreNotTrusted")]
    public async Task Current_storage(string variant)
    {
        await using var f = await EnrolmentFixture.CreateAsync(demoEnabled: variant != "DemoAccountOutsideDemo");
        var reader = f.Services.GetRequiredService<EmployeeAccessReader>();
        var id = variant switch
        {
            "MissingUser" => "absent", "AdministratorWithoutMedewerker" => f.Admin.Id,
            "InternalInstructorWithMedewerkerOwnTraining" or "InstructorWithoutMedewerker" => f.Owner.Id,
            _ => f.Employee.Id
        };
        if (variant == "InstructorWithoutMedewerker") await f.RemoveEmployeeRoleAsync(f.Owner.Id);
        if (variant is "GeneralApprovedAfterRoleAndTargetLoss" or "TrackedOldRolesAreNotTrusted")
        {
            _ = await f.Context.Users.SingleAsync(x => x.Id == id);
            await f.RemoveEmployeeRoleAsync(id);
        }
        if (variant is "Pending" or "Refused" or "Blocked" or "MissingDepartment" or "DemoAccountOutsideDemo" or "GeneralApprovedAfterRoleAndTargetLoss")
            await f.MutateAsync(async db =>
            {
                var user = await db.Users.SingleAsync(x => x.Id == id);
                if (variant == "Pending") user.AccountApprovalStatus = AccountApprovalStatus.Pending;
                if (variant == "Refused") user.AccountApprovalStatus = AccountApprovalStatus.Refused;
                if (variant == "Blocked") user.IsBlocked = true;
                if (variant is "MissingDepartment" or "GeneralApprovedAfterRoleAndTargetLoss") user.DepartmentId = null;
                if (variant == "DemoAccountOutsideDemo") user.DemoSeedKey = "isolated.demo";
            });
        if (variant == "NonexistentDepartment")
            await f.Context.Database.ExecuteSqlInterpolatedAsync($"PRAGMA foreign_keys=OFF; UPDATE AspNetUsers SET DepartmentId=999999 WHERE Id={id}; PRAGMA foreign_keys=ON;");
        var before = await f.DigestAsync();
        var actor = await reader.ReadEmployeeAsync(id);
        Assert.Equal(variant is "ApprovedEmployee" or "InternalInstructorWithMedewerkerOwnTraining" or "NoManagerDoesNotBlock", actor is not null);
        if (variant == "GeneralApprovedAfterRoleAndTargetLoss") Assert.NotNull(await reader.ReadApprovedAsync(id));
        Assert.Equal(before, await f.DigestAsync());
    }
}
