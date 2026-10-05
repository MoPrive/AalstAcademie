using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Accounts;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Accounts;

/// <summary>Een managerkeuze verandert alleen de actor-FK/ConcurrencyStamp; de provider bewijst geen bijbehorende rechtenwrite.</summary>
public class AccountManagerServiceTests
{
    [Theory]
    [InlineData("EmployeeChooseHead")]
    [InlineData("InternalInstructorChooseHead")]
    [InlineData("ClearToNone")]
    [InlineData("ChooseLinkedSelf")]
    [InlineData("PendingDenied")]
    [InlineData("RefusedDenied")]
    [InlineData("ExternalDenied")]
    [InlineData("BlockedDenied")]
    [InlineData("DemoBoundaryDenied")]
    [InlineData("UnknownHeadRejected")]
    [InlineData("UnlinkedHeadRejected")]
    [InlineData("StaleConcurrencyStampConflict")]
    [InlineData("StampChangesSecurityStampPreserved")]
    [InlineData("RolesStatusDepartmentPreserved")]
    [InlineData("RemovedHeadDoesNotRewriteStoredManager")]
    [InlineData("SqlFailureRollbackNoNotification")]
    public async Task Own_fk_only(string variant)
    {
        var interceptor = new EnrolmentFailureInterceptor();
        await using var f = await EnrolmentFixture.CreateAsync(demoEnabled: variant != "DemoBoundaryDenied", interceptor: interceptor);
        var id = variant == "InternalInstructorChooseHead" ? f.Owner.Id : f.Employee.Id;
        await f.LinkHeadAsync(variant == "ChooseLinkedSelf" ? id : f.Owner.Id);
        await f.MutateAsync(async db =>
        {
            var user = await db.Users.SingleAsync(x => x.Id == id);
            if (variant is "ClearToNone" or "RemovedHeadDoesNotRewriteStoredManager") user.ManagerUserId = f.Owner.Id;
            if (variant == "PendingDenied") user.AccountApprovalStatus = AccountApprovalStatus.Pending;
            if (variant == "RefusedDenied") user.AccountApprovalStatus = AccountApprovalStatus.Refused;
            if (variant == "ExternalDenied") user.RequestedAccountType = RequestedAccountType.ExternalInstructor;
            if (variant == "BlockedDenied") user.IsBlocked = true;
            if (variant == "DemoBoundaryDenied") user.DemoSeedKey = "isolated.demo";
            if (variant == "RemovedHeadDoesNotRewriteStoredManager") (await db.Departments.SingleAsync(x => x.Id == f.Employee.DepartmentId)).ResponsibleUserId = null;
        });
        var original = await f.Context.Users.AsNoTracking().SingleAsync(x => x.Id == id);
        var before = await f.DigestAsync();
        var service = f.Services.GetRequiredService<AccountManagerService>();
        if (variant == "RemovedHeadDoesNotRewriteStoredManager")
        { Assert.NotNull((await service.GetAsync(id))?.CurrentManagerName); Assert.Equal(before, await f.DigestAsync()); return; }
        if (variant == "SqlFailureRollbackNoNotification") interceptor.Arm("manager");
        var target = variant switch { "ClearToNone" => null, "ChooseLinkedSelf" => id, "UnknownHeadRejected" => "absent", "UnlinkedHeadRejected" => f.Admin.Id, _ => f.Owner.Id };
        var result = await service.ChangeAsync(id, new AccountManagerInput
        { ManagerUserId = target, ExpectedConcurrencyStamp = variant == "StaleConcurrencyStampConflict" ? Guid.NewGuid().ToString() : original.ConcurrencyStamp });
        var expected = variant switch
        {
            "PendingDenied" or "RefusedDenied" or "ExternalDenied" or "BlockedDenied" or "DemoBoundaryDenied" => TrainingOperationStatus.Forbidden,
            "UnknownHeadRejected" or "UnlinkedHeadRejected" => TrainingOperationStatus.Invalid,
            "StaleConcurrencyStampConflict" => TrainingOperationStatus.Conflict,
            "SqlFailureRollbackNoNotification" => TrainingOperationStatus.StorageUnavailable,
            _ => TrainingOperationStatus.Success
        };
        Assert.Equal(expected, result.Status);
        await using var scope = f.Database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AalstAcademie.Web.Data.ApplicationDbContext>();
        var stored = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id);
        if (result.Succeeded)
        {
            Assert.Equal(target, stored.ManagerUserId);
            Assert.True(original.ConcurrencyStamp != stored.ConcurrencyStamp);
            Assert.True(original.SecurityStamp == stored.SecurityStamp);
            Assert.Equal(original.AccountApprovalStatus, stored.AccountApprovalStatus);
            Assert.Equal(original.DepartmentId, stored.DepartmentId);
            Assert.Equal(variant == "InternalInstructorChooseHead" ? 2 : 1, await db.UserRoles.CountAsync(x => x.UserId == id));
        }
        else Assert.Equal(before, await f.DigestAsync());
        Assert.Equal(0, f.Services.GetRequiredService<RecordingAccountApplicationsNotifier>().Count);
        if (variant == "SqlFailureRollbackNoNotification") Assert.True(interceptor.Triggered);
    }
}
