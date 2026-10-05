using System.Net;
using System.Text.RegularExpressions;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Web;

/// <summary>Nieuwe optionele FK via echte Razor-registratie; verboden invoer maakt geen half account en echoot geen wachtwoord.</summary>
public class AccountManagerRegistrationHttpTests
{
    [Theory]
    [InlineData("EmployeeNone")]
    [InlineData("EmployeeHead")]
    [InlineData("InternalInstructorNone")]
    [InlineData("InternalInstructorHead")]
    [InlineData("ExternalManager400")]
    [InlineData("UnknownManager400")]
    [InlineData("UnlinkedManager400")]
    [InlineData("UniqueHeadsAcrossDepartmentsRendered")]
    [InlineData("LegacyManagerNameOverpostRejected")]
    [InlineData("ReviewDetailsNullAndCurrentManager")]
    public async Task Optional_fk_registration(string variant)
    {
        await using var f = await EnrolmentHttpFixture.CreateAsync(); await f.LinkHeadAsync();
        if (variant == "UniqueHeadsAcrossDepartmentsRendered")
        {
            await f.MutateAsync(async db => (await db.Departments.FirstAsync(x => x.Id != f.Employee.DepartmentId)).ResponsibleUserId = f.Owner.UserId);
            var beforeGet = await f.DigestAsync(); using var get = await f.Browser.GetAsync("/Identity/Account/Register");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode); var html = await get.Content.ReadAsStringAsync();
            Assert.Single(Regex.Matches(html, "<option[^>]*value=\"" + Regex.Escape(f.Owner.UserId) + "\"").Cast<Match>());
            Assert.Contains("data-registration-types=\"1 2\"", html, StringComparison.Ordinal); Assert.Equal(beforeGet, await f.DigestAsync()); return;
        }
        if (variant == "ReviewDetailsNullAndCurrentManager")
        {
            var ids = new List<string>();
            foreach (var chosen in new[] { "", f.Owner.UserId })
            {
                var fields = Form(f, RequestedAccountType.Employee, chosen);
                using var created = await f.Browser.PostFormAsync("/Identity/Account/Register", fields); Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
                await f.Factory.WithServicesAsync(async services => ids.Add(await services.GetRequiredService<ApplicationDbContext>().Users.Where(x => x.Email == fields["Input.Email"]).Select(x => x.Id).SingleAsync()));
            }
            await f.MutateAsync(async db => { (await db.Users.SingleAsync(x => x.Id == f.Owner.UserId)).FirstName = "ActueleHeadNaam"; (await db.Departments.SingleAsync(x => x.Id == f.Employee.DepartmentId)).ResponsibleUserId = null; });
            await f.LoginAsync(f.Admin); var beforeReview = await f.DigestAsync();
            using var none = await f.Browser.GetAsync("/Admin/AccountApplications/" + ids[0]); using var head = await f.Browser.GetAsync("/Admin/AccountApplications/" + ids[1]);
            Assert.Equal(HttpStatusCode.OK, none.StatusCode); Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Contains("Geen", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal); Assert.Contains("ActueleHeadNaam", await head.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(beforeReview, await f.DigestAsync()); return;
        }
        var type = variant.StartsWith("InternalInstructor", StringComparison.Ordinal) ? RequestedAccountType.InternalInstructor :
            variant == "ExternalManager400" ? RequestedAccountType.ExternalInstructor : RequestedAccountType.Employee;
        var manager = variant switch
        { "EmployeeHead" or "InternalInstructorHead" or "ExternalManager400" => f.Owner.UserId, "UnknownManager400" => "not-a-head", "UnlinkedManager400" => f.Employee.UserId, _ => "" };
        var form = Form(f, type, manager);
        if (variant == "LegacyManagerNameOverpostRejected") form["Input.ManagerName"] = "ongewenste vrije tekst";
        var before = await f.DigestAsync(); using var response = await f.Browser.PostFormAsync("/Identity/Account/Register", form);
        var success = variant is "EmployeeNone" or "EmployeeHead" or "InternalInstructorNone" or "InternalInstructorHead";
        Assert.Equal(success ? HttpStatusCode.Redirect : HttpStatusCode.BadRequest, response.StatusCode);
        if (success)
        {
            Assert.Equal("/Account/Status", response.Headers.Location!.OriginalString);
            await f.Factory.WithServicesAsync(async services =>
            {
                var db = services.GetRequiredService<ApplicationDbContext>(); var user = await db.Users.AsNoTracking().SingleAsync(x => x.Email == form["Input.Email"]);
                Assert.Equal(manager.Length == 0 ? null : manager, user.ManagerUserId); Assert.Equal(AccountApprovalStatus.Pending, user.AccountApprovalStatus);
                Assert.False(user.EmailConfirmed); Assert.Empty(await db.UserRoles.Where(x => x.UserId == user.Id).ToListAsync());
                Assert.Equal(type == RequestedAccountType.InternalInstructor, await db.InternalInstructors.AnyAsync(x => x.ApplicationUserId == user.Id));
            });
        }
        else
        {
            Assert.Equal(before, await f.DigestAsync()); var html = await response.Content.ReadAsStringAsync();
            Assert.False(html.Contains(form["Input.Password"], StringComparison.Ordinal));
            Assert.Contains(form["Input.Email"], html, StringComparison.Ordinal);
        }
    }
    private static Dictionary<string, string> Form(EnrolmentHttpFixture f, RequestedAccountType type, string manager)
    {
        var password = AccountWebApplicationFactory.NewPassword();
        var fields = new Dictionary<string, string> { ["Input.RequestedAccountType"] = ((int)type).ToString(),
            ["Input.Email"] = "optional-" + Guid.NewGuid().ToString("N") + "@example.invalid", ["Input.PhoneNumber"] = "0490000000",
            ["Input.Password"] = password, ["Input.ConfirmPassword"] = password, ["Input.ManagerUserId"] = manager };
        if (type == RequestedAccountType.ExternalInstructor) fields["Input.OrganizationName"] = "Fictieve registratieacademie";
        else { fields["Input.FirstName"] = "Mila"; fields["Input.LastName"] = "Test"; fields["Input.DepartmentId"] = f.Employee.DepartmentId!.Value.ToString(); }
        return fields;
    }
}
