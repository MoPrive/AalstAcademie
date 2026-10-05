using System.Net;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Web;

/// <summary>Actuele beheerdersrechten en nullable original-value-CAS worden via de echte mini-formulieren bewezen.</summary>
public class DepartmentResponsiblesHttpTests
{
    [Theory]
    [InlineData("AdminGet")]
    [InlineData("NonAdmin403")]
    [InlineData("AssignPrgOnlyFk")]
    [InlineData("ClearNullPrg")]
    [InlineData("AssignSelfPrg")]
    [InlineData("SameHeadMultipleDepartments")]
    [InlineData("UnknownDepartment404")]
    [InlineData("IneligibleCandidate400")]
    [InlineData("StaleNonNull409Preserved")]
    [InlineData("StaleNull409Preserved")]
    [InlineData("MissingOriginalNullableField400")]
    [InlineData("PostedNameOrIdRejected")]
    [InlineData("MissingCsrf400")]
    [InlineData("GetNoWrite")]
    [InlineData("Storage503PreservesOriginalFk")]
    public async Task Head_fk_only(string variant)
    {
        await using var f = await EnrolmentHttpFixture.CreateAsync();
        if (variant != "NonAdmin403") await f.LoginAsync(f.Admin);
        if (variant == "AssignSelfPrg")
            await f.MutateAsync(async db => { var u = await db.Users.SingleAsync(x => x.Id == f.Admin.UserId); u.FirstName = "Demi"; u.LastName = "Beheerder"; });
        if (variant is "AdminGet" or "NonAdmin403" or "GetNoWrite")
        {
            var beforeRead = await f.DigestAsync(); using var get = await f.Browser.GetAsync("/DepartmentResponsibles");
            Assert.Equal(variant == "NonAdmin403" ? HttpStatusCode.Forbidden : HttpStatusCode.OK, get.StatusCode);
            if (variant != "NonAdmin403") { var html = await get.Content.ReadAsStringAsync(); Assert.Contains("ExpectedResponsibleUserId", html, StringComparison.Ordinal); Assert.DoesNotContain(f.Owner.Email, html, StringComparison.Ordinal); }
            Assert.Equal(beforeRead, await f.DigestAsync()); return;
        }
        if (variant is "ClearNullPrg" or "StaleNonNull409Preserved") await f.LinkHeadAsync();
        Department original = null!;
        await f.Factory.WithServicesAsync(async services => original = await services.GetRequiredService<ApplicationDbContext>().Departments.AsNoTracking().SingleAsync(x => x.Id == f.Employee.DepartmentId));
        var target = variant switch { "ClearNullPrg" => "", "AssignSelfPrg" => f.Admin.UserId, "IneligibleCandidate400" => "absent", _ => f.Owner.UserId };
        var form = new Dictionary<string, string> { ["ResponsibleUserId"] = target, ["ExpectedResponsibleUserId"] = original.ResponsibleUserId ?? "" };
        if (variant == "StaleNonNull409Preserved") await f.LinkHeadAsync(f.Employee.UserId);
        if (variant == "StaleNull409Preserved") await f.LinkHeadAsync();
        if (variant == "MissingOriginalNullableField400") form.Remove("ExpectedResponsibleUserId");
        if (variant == "PostedNameOrIdRejected") { form["Name"] = "forged department"; form["Id"] = "999"; }
        if (variant == "Storage503PreservesOriginalFk") f.Fault.Arm("responsible");
        var id = variant == "UnknownDepartment404" ? int.MaxValue : original.Id;
        var path = "/DepartmentResponsibles/Assign/" + id;
        var before = await f.DigestAsync();
        using var response = variant == "MissingCsrf400" ? await f.Browser.RawClient.PostAsync(path, new FormUrlEncodedContent(form)) : await f.PostAsync(path, form);
        var expected = variant switch
        {
            "AssignPrgOnlyFk" or "ClearNullPrg" or "AssignSelfPrg" or "SameHeadMultipleDepartments" => HttpStatusCode.Redirect,
            "UnknownDepartment404" => HttpStatusCode.NotFound,
            "StaleNonNull409Preserved" or "StaleNull409Preserved" => HttpStatusCode.Conflict,
            "Storage503PreservesOriginalFk" => HttpStatusCode.ServiceUnavailable,
            _ => HttpStatusCode.BadRequest
        };
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Redirect)
        {
            Assert.Equal("/DepartmentResponsibles", response.Headers.Location!.OriginalString);
            if (variant == "SameHeadMultipleDepartments")
            {
                int second = 0; await f.Factory.WithServicesAsync(async services => second = await services.GetRequiredService<ApplicationDbContext>().Departments.Where(x => x.Id != original.Id).Select(x => x.Id).FirstAsync());
                using var other = await f.PostAsync("/DepartmentResponsibles/Assign/" + second, new() { ["ResponsibleUserId"] = target, ["ExpectedResponsibleUserId"] = "" }); Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
            }
            await f.Factory.WithServicesAsync(async services =>
            {
                var db = services.GetRequiredService<ApplicationDbContext>(); var stored = await db.Departments.AsNoTracking().SingleAsync(x => x.Id == original.Id);
                Assert.Equal(original.Name, stored.Name); Assert.Equal(target == "" ? null : target, stored.ResponsibleUserId);
                Assert.Equal(4, await db.Departments.CountAsync()); Assert.Null((await db.Users.SingleAsync(x => x.Id == f.Employee.UserId)).ManagerUserId);
                if (variant == "SameHeadMultipleDepartments") Assert.Equal(2, await db.Departments.CountAsync(x => x.ResponsibleUserId == target));
            });
        }
        else
        {
            Assert.Equal(before, await f.DigestAsync());
            if (expected is HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable)
            {
                var html = EnrolmentHttpFixture.FormHtml(await response.Content.ReadAsStringAsync(), path);
                Assert.Equal(form["ExpectedResponsibleUserId"], EnrolmentHttpFixture.Value(html, "ExpectedResponsibleUserId"));
            }
            if (variant == "Storage503PreservesOriginalFk") Assert.True(f.Fault.Triggered);
        }
    }
}
