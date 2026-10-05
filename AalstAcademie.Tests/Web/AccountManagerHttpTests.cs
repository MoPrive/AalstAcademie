using System.Net;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Web;

/// <summary>De HTTP-keuze wijzigt alleen de actor-FK/ConcurrencyStamp; geen rollen, goedkeuring of SecurityStamp.</summary>
public class AccountManagerHttpTests
{
    [Theory]
    [InlineData("EmployeeGetNoneAndOptions")]
    [InlineData("InternalInstructorGet")]
    [InlineData("External403")]
    [InlineData("PendingDenied")]
    [InlineData("PostLinkedManagerPrg")]
    [InlineData("PostNonePrg")]
    [InlineData("PostLinkedSelfPrg")]
    [InlineData("StaleStamp409Preserved")]
    [InlineData("InvalidHead400PreservedChoice")]
    [InlineData("PostedOtherActorRolesSecurityStampRejected")]
    [InlineData("MissingCsrf400")]
    [InlineData("GetNoWrite")]
    [InlineData("CurrentNameAfterHeadRemoval")]
    [InlineData("Storage503KeepsOriginalStamp")]
    public async Task Own_manager(string variant)
    {
        await using var f = await EnrolmentHttpFixture.CreateAsync();
        await f.LinkHeadAsync(variant == "PostLinkedSelfPrg" ? f.Employee.UserId : null);
        if (variant == "InternalInstructorGet") await f.LoginAsync(f.Owner);
        if (variant == "External403") await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.UserId)).RequestedAccountType = RequestedAccountType.ExternalInstructor);
        if (variant == "PendingDenied") await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.UserId)).AccountApprovalStatus = AccountApprovalStatus.Pending);
        if (variant is "PostNonePrg" or "CurrentNameAfterHeadRemoval")
            await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.UserId)).ManagerUserId = f.Owner.UserId);
        if (variant == "CurrentNameAfterHeadRemoval")
            await f.MutateAsync(async db => { (await db.Departments.SingleAsync(x => x.Id == f.Employee.DepartmentId)).ResponsibleUserId = null; (await db.Users.SingleAsync(x => x.Id == f.Owner.UserId)).FirstName = "ElsActueel"; });
        if (variant is "EmployeeGetNoneAndOptions" or "InternalInstructorGet" or "External403" or "PendingDenied" or "GetNoWrite" or "CurrentNameAfterHeadRemoval")
        {
            var beforeRead = await f.DigestAsync(); using var read = await f.Browser.GetAsync("/AccountManager");
            Assert.Equal(variant switch { "External403" => HttpStatusCode.Forbidden, "PendingDenied" => HttpStatusCode.Redirect, _ => HttpStatusCode.OK }, read.StatusCode);
            var html = await read.Content.ReadAsStringAsync();
            if (variant == "EmployeeGetNoneAndOptions") { Assert.Contains(">Geen</option>", html, StringComparison.Ordinal); Assert.Contains($"value=\"{f.Owner.UserId}\"", html, StringComparison.Ordinal); }
            if (variant == "CurrentNameAfterHeadRemoval") Assert.Contains("ElsActueel", html, StringComparison.Ordinal);
            if (variant == "PendingDenied") Assert.Equal("/Account/Status", read.Headers.Location!.OriginalString);
            Assert.Equal(beforeRead, await f.DigestAsync()); return;
        }
        ApplicationUser original = null!;
        await f.Factory.WithServicesAsync(async services => original = await services.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(x => x.Id == f.Employee.UserId));
        var target = variant switch { "PostNonePrg" => "", "PostLinkedSelfPrg" => f.Employee.UserId, "InvalidHead400PreservedChoice" => "unavailable-head", _ => f.Owner.UserId };
        var form = new Dictionary<string, string> { ["ManagerUserId"] = target, ["ExpectedConcurrencyStamp"] = original.ConcurrencyStamp! };
        if (variant == "StaleStamp409Preserved") await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.UserId)).ConcurrencyStamp = Guid.NewGuid().ToString());
        if (variant == "PostedOtherActorRolesSecurityStampRejected") { form["ApplicationUserId"] = f.Owner.UserId; form["Roles"] = RoleNames.Beheerder; form["SecurityStamp"] = "forged"; }
        if (variant == "Storage503KeepsOriginalStamp") f.Fault.Arm("manager");
        var before = await f.DigestAsync();
        using var response = variant == "MissingCsrf400" ? await f.Browser.RawClient.PostAsync("/AccountManager", new FormUrlEncodedContent(form)) : await f.PostAsync("/AccountManager", form);
        var expected = variant switch
        { "PostLinkedManagerPrg" or "PostNonePrg" or "PostLinkedSelfPrg" => HttpStatusCode.Redirect, "StaleStamp409Preserved" => HttpStatusCode.Conflict, "Storage503KeepsOriginalStamp" => HttpStatusCode.ServiceUnavailable, _ => HttpStatusCode.BadRequest };
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Redirect)
        {
            Assert.Equal("/AccountManager", response.Headers.Location!.OriginalString);
            await f.Factory.WithServicesAsync(async services =>
            {
                var db = services.GetRequiredService<ApplicationDbContext>(); var stored = await db.Users.AsNoTracking().SingleAsync(x => x.Id == f.Employee.UserId);
                Assert.Equal(target.Length == 0 ? null : target, stored.ManagerUserId); Assert.NotEqual(original.ConcurrencyStamp, stored.ConcurrencyStamp);
                Assert.Equal(original.SecurityStamp, stored.SecurityStamp); Assert.Equal(original.DepartmentId, stored.DepartmentId); Assert.Equal(original.AccountApprovalStatus, stored.AccountApprovalStatus);
                Assert.Equal(1, await db.UserRoles.CountAsync(x => x.UserId == stored.Id));
            });
        }
        else
        {
            Assert.Equal(before, await f.DigestAsync());
            if (variant != "MissingCsrf400")
            { var html = await response.Content.ReadAsStringAsync(); Assert.Equal(form["ExpectedConcurrencyStamp"], EnrolmentHttpFixture.Value(html, "ExpectedConcurrencyStamp")); Assert.Contains(target, html, StringComparison.Ordinal); }
            if (variant == "Storage503KeepsOriginalStamp") Assert.True(f.Fault.Triggered);
        }
    }
}
