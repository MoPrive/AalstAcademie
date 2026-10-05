using AalstAcademie.Tests.Enrolment;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Accounts;

/// <summary>Twee actuele beheerders vergelijken dezelfde originele nullable FK; kandidaat en actor worden na wachten herlezen.</summary>
[Collection("Revision02 provider races")]
public class DepartmentResponsibleConcurrencyTests
{
    [Theory]
    [InlineData("TwoAdminsSameOriginalNull")]
    [InlineData("TwoAdminsSameOriginalNonNull")]
    [InlineData("CandidateBlockedBeforeAssignLock")]
    [InlineData("AdminRoleRevokedBeforeAssignLock")]
    public async Task Original_fk_and_current_authorization(string variant)
    {
        await using var f = await EnrolmentRaceFixture.CreateAsync();
        var otherAdmin = await TestAccountData.CreateAdministratorAsync(f.Services);
        var nonNull = variant == "TwoAdminsSameOriginalNonNull";
        if (nonNull) await f.Read(async db => { (await db.Departments.SingleAsync(x => x.Id == f.Employee.DepartmentId)).ResponsibleUserId = f.Owner.Id; await db.SaveChangesAsync(); });
        var original = nonNull ? f.Owner.Id : null;
        var before = await AccountsDigest(f);
        Task<TrainingOperationResult> Assign(IServiceProvider sp, string actor, string? candidate) => sp.GetRequiredService<DepartmentResponsibleService>().AssignAsync(actor, f.Employee.DepartmentId!.Value,
            new() { ExpectedResponsibleUserId = original, ResponsibleUserId = candidate });
        TrainingOperationResult[] result;
        if (variant.StartsWith("TwoAdmins", StringComparison.Ordinal))
            result = await f.Race(sp => Assign(sp, f.Admin.Id, nonNull ? f.Employee.Id : f.Owner.Id),
                sp => Assign(sp, otherAdmin.Id, nonNull ? null : f.Employee.Id));
        else if (variant == "CandidateBlockedBeforeAssignLock")
            result = await f.Race(sp => f.Write(sp, async db => (await db.Users.SingleAsync(x => x.Id == f.Owner.Id)).IsBlocked = true), sp => Assign(sp, f.Admin.Id, f.Owner.Id));
        else result = await f.Race(sp => f.Revoke(sp, f.Admin.Id, RoleNames.Beheerder), sp => Assign(sp, f.Admin.Id, f.Owner.Id));
        Assert.True(result[0].Succeeded);
        Assert.Equal(variant.StartsWith("TwoAdmins", StringComparison.Ordinal) ? TrainingOperationStatus.Conflict : variant.Contains("Candidate", StringComparison.Ordinal)
            ? TrainingOperationStatus.Invalid : TrainingOperationStatus.Forbidden, result[1].Status);
        await f.Read(async db =>
        {
            Assert.Equal(variant.StartsWith("TwoAdmins", StringComparison.Ordinal) ? nonNull ? f.Employee.Id : f.Owner.Id : null,
                (await db.Departments.SingleAsync(x => x.Id == f.Employee.DepartmentId)).ResponsibleUserId);
            Assert.Equal(0, await db.Users.CountAsync(x => x.ManagerUserId != null));
            Assert.Equal(0, await db.Registrations.CountAsync()); Assert.Equal(0, await db.WaitlistEntries.CountAsync());
        });
        // Een geslaagde headwrite verandert geen enkele Identity-kolom of grant.
        if (variant.StartsWith("TwoAdmins", StringComparison.Ordinal)) Assert.Equal(before, await AccountsDigest(f));
    }
    private static async Task<string> AccountsDigest(EnrolmentRaceFixture f)
    {
        string result = "";
        await f.Read(async db =>
        {
            // De verwachte afdelings-FK valt buiten deze vergelijking; alle accountkolommen en grants blijven erin.
            var payload = System.Text.Json.JsonSerializer.Serialize(new {
                Users = await db.Users.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
                Roles = await db.UserRoles.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.RoleId).ToArrayAsync(),
                Internal = await db.InternalInstructors.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
                External = await db.ExternalInstructors.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync() });
            result = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
        });
        return result;
    }
}
