using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Accounts;

public class DepartmentResponsibleQueriesTests
{
    [Theory]
    [InlineData("ExistingDepartmentsOnly")]
    [InlineData("ApprovedPersonalNameCandidates")]
    [InlineData("BlockedAndPendingExcluded")]
    [InlineData("DemoBoundaryCandidateExcluded")]
    [InlineData("CandidatesNeedNoDepartmentOrRoleMatch")]
    [InlineData("NonAdminQueryDenied")]
    public async Task Minimal_current_projection(string variant)
    {
        await using var f = await EnrolmentFixture.CreateAsync(demoEnabled: variant != "DemoBoundaryCandidateExcluded");
        if (variant is "BlockedAndPendingExcluded" or "DemoBoundaryCandidateExcluded" or "CandidatesNeedNoDepartmentOrRoleMatch")
            await f.MutateAsync(async db => { var user = await db.Users.SingleAsync(x => x.Id == f.Employee.Id);
                if (variant == "BlockedAndPendingExcluded") user.IsBlocked = true;
                if (variant == "DemoBoundaryCandidateExcluded") user.DemoSeedKey = "test.demo";
                if (variant == "CandidatesNeedNoDepartmentOrRoleMatch") { user.DepartmentId = null; db.UserRoles.RemoveRange(await db.UserRoles.Where(x => x.UserId == user.Id).ToListAsync()); } });
        var before = await f.DigestAsync(); var result = await f.Services.GetRequiredService<DepartmentResponsibleQueries>()
            .GetAsync(variant == "NonAdminQueryDenied" ? f.Employee.Id : f.Admin.Id);
        if (variant == "NonAdminQueryDenied") Assert.Null(result);
        else
        {
            Assert.Equal(4, result!.Departments.Count);
            Assert.DoesNotContain(result.Candidates, x => x.UserId is null && x.DisplayName != "Geen");
            Assert.Equal(variant is not ("BlockedAndPendingExcluded" or "DemoBoundaryCandidateExcluded"), result.Candidates.Any(x => x.UserId == f.Employee.Id));
            Assert.Contains(result.Candidates, x => x.UserId == f.Admin.Id);
        }
        Assert.Equal(before, await f.DigestAsync());
    }
}
