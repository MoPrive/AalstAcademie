using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Accounts;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Accounts;

public class DepartmentResponsibleServiceTests
{
    [Theory]
    [InlineData("AdminAssign")]
    [InlineData("AdminClear")]
    [InlineData("AdminAssignSelf")]
    [InlineData("SameHeadMultipleDepartments")]
    [InlineData("NoAutoEmployeeManagerChange")]
    [InlineData("NonAdminDirectServiceDenied")]
    [InlineData("BlockedAdminDenied")]
    [InlineData("RoleRevokedAfterPriorRead")]
    [InlineData("UnknownDepartmentNotFound")]
    [InlineData("UnknownCandidateInvalid")]
    [InlineData("PendingCandidateInvalid")]
    [InlineData("BlockedCandidateInvalid")]
    [InlineData("DemoBoundaryCandidateInvalid")]
    [InlineData("StaleExpectedNonNullConflict")]
    [InlineData("StaleExpectedNullConflict")]
    [InlineData("SqlFailureRollbackOnlyFk")]
    public async Task Current_admin_and_original_fk(string variant)
    {
        var interceptor = new EnrolmentFailureInterceptor();
        await using var f = await EnrolmentFixture.CreateAsync(demoEnabled: variant != "DemoBoundaryCandidateInvalid", interceptor: interceptor);
        if (variant is "AdminClear" or "NoAutoEmployeeManagerChange" or "StaleExpectedNullConflict") await f.LinkHeadAsync();
        if (variant == "NoAutoEmployeeManagerChange") await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.Id)).ManagerUserId = f.Owner.Id);
        if (variant == "RoleRevokedAfterPriorRead") _ = await f.Services.GetRequiredService<DepartmentResponsibleQueries>().GetAsync(f.Admin.Id);
        await f.MutateAsync(async db =>
        {
            if (variant == "BlockedAdminDenied") (await db.Users.SingleAsync(x => x.Id == f.Admin.Id)).IsBlocked = true;
            if (variant == "RoleRevokedAfterPriorRead") db.UserRoles.RemoveRange(await db.UserRoles.Where(x => x.UserId == f.Admin.Id).ToListAsync());
            var candidate = await db.Users.SingleAsync(x => x.Id == f.Owner.Id);
            if (variant == "PendingCandidateInvalid") candidate.AccountApprovalStatus = AccountApprovalStatus.Pending;
            if (variant == "BlockedCandidateInvalid") candidate.IsBlocked = true;
            if (variant == "DemoBoundaryCandidateInvalid") candidate.DemoSeedKey = "isolated.demo";
        });
        var department = await f.Context.Departments.AsNoTracking().SingleAsync(x => x.Id == f.Employee.DepartmentId);
        var before = await f.DigestAsync();
        if (variant == "SqlFailureRollbackOnlyFk") interceptor.Arm("responsible");
        var target = variant switch { "AdminClear" => null, "AdminAssignSelf" or "NoAutoEmployeeManagerChange" => f.Admin.Id, "UnknownCandidateInvalid" => "absent", _ => f.Owner.Id };
        var expectedFk = variant switch { "StaleExpectedNonNullConflict" => "outdated", "StaleExpectedNullConflict" => null, _ => department.ResponsibleUserId };
        var service = f.Services.GetRequiredService<DepartmentResponsibleService>();
        var result = await service.AssignAsync(variant == "NonAdminDirectServiceDenied" ? f.Employee.Id : f.Admin.Id,
            variant == "UnknownDepartmentNotFound" ? int.MaxValue : department.Id,
            new DepartmentResponsibleInput { ResponsibleUserId = target, ExpectedResponsibleUserId = expectedFk });
        var status = variant switch
        {
            "NonAdminDirectServiceDenied" or "BlockedAdminDenied" or "RoleRevokedAfterPriorRead" => TrainingOperationStatus.Forbidden,
            "UnknownDepartmentNotFound" => TrainingOperationStatus.NotFound,
            "UnknownCandidateInvalid" or "PendingCandidateInvalid" or "BlockedCandidateInvalid" or "DemoBoundaryCandidateInvalid" => TrainingOperationStatus.Invalid,
            "StaleExpectedNonNullConflict" or "StaleExpectedNullConflict" => TrainingOperationStatus.Conflict,
            "SqlFailureRollbackOnlyFk" => TrainingOperationStatus.StorageUnavailable,
            _ => TrainingOperationStatus.Success
        };
        Assert.Equal(status, result.Status);
        await using var scope = f.Database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AalstAcademie.Web.Data.ApplicationDbContext>();
        if (result.Succeeded)
        {
            var stored = await db.Departments.AsNoTracking().SingleAsync(x => x.Id == department.Id);
            Assert.Equal(target, stored.ResponsibleUserId); Assert.Equal(department.Name, stored.Name);
            Assert.Equal(variant == "NoAutoEmployeeManagerChange" ? f.Owner.Id : null,
                (await db.Users.AsNoTracking().SingleAsync(x => x.Id == f.Employee.Id)).ManagerUserId);
            if (variant == "SameHeadMultipleDepartments")
            {
                var other = await db.Departments.AsNoTracking().Where(x => x.Id != department.Id).FirstAsync();
                Assert.True((await scope.ServiceProvider.GetRequiredService<DepartmentResponsibleService>().AssignAsync(f.Admin.Id, other.Id,
                    new() { ResponsibleUserId = target, ExpectedResponsibleUserId = other.ResponsibleUserId })).Succeeded);
                Assert.Equal(2, await db.Departments.CountAsync(x => x.ResponsibleUserId == target));
            }
        }
        else Assert.Equal(before, await f.DigestAsync());
        if (variant == "SqlFailureRollbackOnlyFk") Assert.True(interceptor.Triggered);
        Assert.Equal(0, f.Services.GetRequiredService<RecordingAccountApplicationsNotifier>().Count);
    }
}
