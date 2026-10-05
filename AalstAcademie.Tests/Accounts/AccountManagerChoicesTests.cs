using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Accounts;

public class AccountManagerChoicesTests
{
    [Theory]
    [InlineData("NoneOption")]
    [InlineData("AllLinkedHeadsAcrossDepartments")]
    [InlineData("SameHeadDeduplicated")]
    [InlineData("PersonalNameThenIdOrder")]
    [InlineData("SelfIfLinked")]
    [InlineData("UnknownIdNotAllowed")]
    [InlineData("ExistingUserNotLinkedNotAllowed")]
    [InlineData("RemovedHeadOldManagerStillReadable")]
    [InlineData("NoContactRoleStampProjection")]
    public async Task Minimal_current_choices(string variant)
    {
        await using var f = await EnrolmentFixture.CreateAsync();
        var choices = f.Services.GetRequiredService<AccountManagerChoices>();
        await f.LinkHeadAsync(variant == "SelfIfLinked" ? f.Employee.Id : f.Owner.Id);
        if (variant is "AllLinkedHeadsAcrossDepartments" or "SameHeadDeduplicated" or "PersonalNameThenIdOrder")
            await f.MutateAsync(async db => (await db.Departments.Where(x => x.Id != f.Employee.DepartmentId).FirstAsync()).ResponsibleUserId =
                variant == "SameHeadDeduplicated" ? f.Owner.Id : f.Admin.Id);
        if (variant == "RemovedHeadOldManagerStillReadable")
            await f.MutateAsync(async db =>
            {
                (await db.Users.SingleAsync(x => x.Id == f.Employee.Id)).ManagerUserId = f.Owner.Id;
                (await db.Departments.SingleAsync(x => x.Id == f.Employee.DepartmentId)).ResponsibleUserId = null;
            });
        var before = await f.DigestAsync();
        var rows = await choices.GetAsync();
        Assert.Equal("Geen", rows[0].DisplayName); Assert.Null(rows[0].UserId);
        Assert.Equal(rows.Count, rows.Select(x => x.UserId).Distinct().Count());
        Assert.Equal(variant is "AllLinkedHeadsAcrossDepartments" or "PersonalNameThenIdOrder" ? 3
            : variant == "RemovedHeadOldManagerStillReadable" ? 1 : 2, rows.Count);
        if (variant == "PersonalNameThenIdOrder")
            Assert.Equal(rows.Skip(1).OrderBy(x => x.DisplayName, StringComparer.Ordinal).ThenBy(x => x.UserId, StringComparer.Ordinal), rows.Skip(1));
        if (variant == "SelfIfLinked") Assert.Contains(rows, x => x.UserId == f.Employee.Id);
        if (variant is "UnknownIdNotAllowed" or "ExistingUserNotLinkedNotAllowed")
            Assert.False(await choices.IsAllowedAsync(variant == "UnknownIdNotAllowed" ? "absent" : f.Admin.Id));
        if (variant == "RemovedHeadOldManagerStillReadable")
        { var profile = await f.Services.GetRequiredService<AccountManagerService>().GetAsync(f.Employee.Id); Assert.NotNull(profile?.CurrentManagerName); }
        if (variant == "NoContactRoleStampProjection")
            Assert.Equal(new[] { "UserId", "DisplayName" }.Order(), rows[0].GetType().GetProperties().Select(x => x.Name).Order());
        Assert.Equal(before, await f.DigestAsync());
    }
}
