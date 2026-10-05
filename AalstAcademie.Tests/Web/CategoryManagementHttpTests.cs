// Sprint 003: Gebruikt echte cookies en antiforgery voor categorieaanmaak en hernoemen
// Controleert autorisatie, onbekende velden, naamconflicten en behoud van het geposte versietoken.
using System.Net;
using System.Text.RegularExpressions;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Controllers;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Web;

/// <summary>18 echte HTTP-cases toetsen categoriebeheer, actuele autorisatie, naamuniciteit en versiebehoud.</summary>
public class CategoryManagementHttpTests
{
    // CH1: create en rename volgen PRG; de onafhankelijke scope leest de genormaliseerde naam en nieuwe versie.
    [Fact]
    public async Task Create_and_rename_redirect_and_roundtrip_name_key_and_version()
    {
        await using var test = await Session.CreateAsync();
        using var created = await test.Browser.PostFormAsync("/Categories/Create", new Dictionary<string, string> { ["Name"] = "  HTTP   Categorie  " });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var row = await SingleCategoryAsync(test.Factory); Assert.Equal("HTTP Categorie", row.Name); Assert.Equal("HTTP CATEGORIE", row.NormalizedName); Assert.NotEqual(Guid.Empty, row.Version);
        var owner = await test.Factory.AddAccountAsync(RequestedAccountType.InternalInstructor, AccountApprovalStatus.Approved, [RoleNames.Lesgever]);
        AalstAcademie.Web.Models.Domain.Training? linked = null;
        await test.Factory.WithServicesAsync(async services =>
        {
            var result = await services.GetRequiredService<TrainingManagementService>().CreateAsync(test.Admin.UserId,
                new(new("Gekoppelde opleidingsdefinitie", null, row.Id, null, false), owner.UserId));
            Assert.True(result.Succeeded);
            linked = await services.GetRequiredService<ApplicationDbContext>().Trainings.AsNoTracking().SingleAsync(x => x.Id == result.Id);
        });
        var identity = await DigestAsync(test.Factory, false);
        using var edit = await test.Browser.GetAsync($"/Categories/Edit/{row.Id}"); Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        AssertFormValue(await edit.Content.ReadAsStringAsync(), "ExpectedVersion", row.Version.ToString());
        using var renamed = await test.Browser.PostFormAsync($"/Categories/Edit/{row.Id}", new Dictionary<string, string> { ["Name"] = "Nieuwe HTTP categorie", ["ExpectedVersion"] = row.Version.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, renamed.StatusCode);
        var after = await ReadAsync(test.Factory, row.Id); Assert.Equal("Nieuwe HTTP categorie", after.Name); Assert.Equal("NIEUWE HTTP CATEGORIE", after.NormalizedName); Assert.NotEqual(row.Version, after.Version);
        await test.Factory.WithServicesAsync(async services =>
        {
            var training = await services.GetRequiredService<ApplicationDbContext>().Trainings.AsNoTracking().Include(x => x.Category).SingleAsync(x => x.Id == linked!.Id);
            Assert.Equal(linked!.CategoryId, training.CategoryId); Assert.Equal(linked.Version, training.Version);
            Assert.Equal(linked.InstructorUserId, training.InstructorUserId); Assert.Equal(linked.Title, training.Title);
            Assert.Equal("Nieuwe HTTP categorie", training.Category.Name);
        });
        Assert.Equal(identity, await DigestAsync(test.Factory, false));
    }

    // CH2: HTML GET en geldige CSRF-POST controleren actuele rollen/status; oude beheerdersclaims zijn onvoldoende.
    [Theory]
    [InlineData("employee")] [InlineData("internal")] [InlineData("external")] [InlineData("Pending")] [InlineData("revoked-admin")]
    public async Task Nonadministrators_and_ineligible_sessions_cannot_read_or_mutate_categories(string variant)
    {
        await using var factory = new AccountWebApplicationFactory();
        var type = variant == "revoked-admin" ? (RequestedAccountType?)null : variant == "external" ? RequestedAccountType.ExternalInstructor : variant == "internal" ? RequestedAccountType.InternalInstructor : RequestedAccountType.Employee;
        var roles = variant == "revoked-admin" ? new[] { RoleNames.Beheerder } : variant is "internal" or "external" ? new[] { RoleNames.Lesgever } : new[] { RoleNames.Medewerker };
        var actor = await factory.AddAccountAsync(type, AccountApprovalStatus.Approved, roles);
        using var browser = factory.CreateIdentityClient(); await LoginAsync(browser, actor);
        var row = await PrepareCategoryAsync(factory); var token = await browser.GetAntiforgeryTokenAsync(variant == "revoked-admin" ? "/Categories/Create" : "/Identity/Account/Login");
        if (variant is "Pending" or "revoked-admin") await factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>(); var user = await db.Users.SingleAsync(x => x.Id == actor.UserId);
            if (variant == "Pending") { user.AccountApprovalStatus = AccountApprovalStatus.Pending; await db.SaveChangesAsync(); }
            else
            {
                var stamp = user.SecurityStamp;
                Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().RemoveFromRoleAsync(user, RoleNames.Beheerder)).Succeeded);
                Assert.Equal(stamp, user.SecurityStamp);
            }
        });
        var before = await DigestAsync(factory);
        foreach (var path in new[] { "/Categories", "/Categories/Create", $"/Categories/Edit/{row.Id}" })
        {
            using var get = await browser.GetAsync(path); AssertDenial(get, variant);
        }
        using var create = await PostAsync(browser, "/Categories/Create", new() { ["Name"] = "Geldige categorie" }, token); AssertDenial(create, variant);
        using var rename = await PostAsync(browser, $"/Categories/Edit/{row.Id}", new() { ["Name"] = "Geldige nieuwe naam", ["ExpectedVersion"] = row.Version.ToString() }, token); AssertDenial(rename, variant);
        Assert.Equal(before, await DigestAsync(factory));
    }

    // CH3: elke naamfout wordt zowel bij create als rename gecontroleerd; duplicate keys blijven veldfouten.
    [Theory] [InlineData("blank")] [InlineData("101")] [InlineData("case-whitespace")] [InlineData("FormC")]
    public async Task Invalid_or_normalized_duplicate_name_is_400_without_a_write(string variant)
    {
        await using var test = await Session.CreateAsync();
        var existing = await PrepareCategoryAsync(test.Factory, variant == "FormC" ? "Café" : "Fictieve categorie");
        var target = await PrepareCategoryAsync(test.Factory, "Andere fictieve categorie");
        var name = variant switch { "blank" => " \t", "101" => new string('a', 101), "case-whitespace" => "  FICTIEVE   categorie  ", _ => "Cafe\u0301" };
        var before = await DigestAsync(test.Factory);
        using var create = await test.Browser.PostFormAsync("/Categories/Create", new Dictionary<string, string> { ["Name"] = name });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode); AssertFormValue(await create.Content.ReadAsStringAsync(), "Name", name);
        using var rename = await test.Browser.PostFormAsync($"/Categories/Edit/{target.Id}", new Dictionary<string, string> { ["Name"] = name, ["ExpectedVersion"] = target.Version.ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
        var html = await rename.Content.ReadAsStringAsync(); Assert.Contains("validation-summary-errors", html); AssertFormValue(html, "Name", name); AssertFormValue(html, "ExpectedVersion", target.Version.ToString());
        Assert.Equal(before, await DigestAsync(test.Factory)); Assert.Equal(existing, await ReadAsync(test.Factory, existing.Id));
    }

    // CH4: vier onbruikbare wirevormen zijn400; een echt eerder gelezen versie blijft409 met oorspronkelijk formulier.
    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("empty-guid")]
    [InlineData("malformed")]
    [InlineData("stale")]
    public async Task Category_wire_version_is_strict_and_preserves_original_input(string variant)
    {
        await using var test = await Session.CreateAsync(); var row = await PrepareCategoryAsync(test.Factory);
        if (variant == "stale")
        {
            await test.Factory.WithServicesAsync(async services => Assert.True((await services.GetRequiredService<CategoryManagementService>()
                .RenameAsync(test.Admin.UserId, new(row.Id, row.Version, "Onafhankelijk bijgewerkte categorie"))).Succeeded));
            Assert.NotEqual(row.Version, (await ReadAsync(test.Factory, row.Id)).Version);
        }
        var version = variant switch { "missing" => null, "blank" => "", "empty-guid" => Guid.Empty.ToString(), "malformed" => "geen-guid", _ => row.Version.ToString() };
        var form = new Dictionary<string, string> { ["Name"] = "Mijn open categorieformulier" };
        if (version is not null) form["ExpectedVersion"] = version;
        var before = await DigestAsync(test.Factory);
        using var response = await test.Browser.PostFormAsync($"/Categories/Edit/{row.Id}", form);
        Assert.Equal(variant == "stale" ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await DigestAsync(test.Factory));
        var html = await response.Content.ReadAsStringAsync(); AssertFormValue(html, "Name", form["Name"]); AssertFormValue(html, "ExpectedVersion", version ?? "");
        if (variant == "stale") Assert.Matches("(?is)<a\\b[^>]*href\\s*=\\s*[\"']" + Regex.Escape($"/Categories/Edit/{row.Id}") + "(?:\\?[^\"']*)?[\"']", html);
    }

    // CH5: zelfs een actuele beheerder kan de categorie niet wijzigen zonder antiforgery.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Create_and_rename_require_antiforgery(bool rename)
    {
        await using var test = await Session.CreateAsync(); var row = await PrepareCategoryAsync(test.Factory);
        var form = new Dictionary<string, string> { ["Name"] = "Geldige categorie zonder token" };
        if (rename) form["ExpectedVersion"] = row.Version.ToString(); var path = rename ? $"/Categories/Edit/{row.Id}" : "/Categories/Create";
        var before = await DigestAsync(test.Factory);
        using var response = await test.Browser.RawClient.PostAsync(path, new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
    }

    // CH6: GET404 en POST/DELETE404/405 sluiten samen met endpointmetadata een deletefunctie uit; rij en volledige digest blijven gelijk.
    [Fact]
    public async Task Category_delete_route_and_endpoint_are_absent_and_category_is_preserved()
    {
        await using var test = await Session.CreateAsync(); var row = await PrepareCategoryAsync(test.Factory);
        var token = await test.Browser.GetAntiforgeryTokenAsync("/Categories/Create"); var before = await DigestAsync(test.Factory);
        var path = $"/Categories/Delete/{row.Id}";
        using var get = await test.Browser.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var post = await PostAsync(test.Browser, path, new() { ["ExpectedVersion"] = row.Version.ToString() }, token); Assert.Contains(post.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        using var request = new HttpRequestMessage(HttpMethod.Delete, path) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }) };
        using var delete = await test.Browser.RawClient.SendAsync(request); Assert.Contains(delete.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        var actions = test.Factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items.OfType<ControllerActionDescriptor>().Where(x => x.ControllerTypeInfo.AsType() == typeof(CategoriesController)).ToArray();
        Assert.NotEmpty(actions); Assert.DoesNotContain(actions, x => x.ActionName.Contains("Delete", StringComparison.OrdinalIgnoreCase) || x.ActionName.Contains("Remove", StringComparison.OrdinalIgnoreCase)
            || (x.ActionConstraints?.OfType<HttpMethodActionConstraint>().Any(c => c.HttpMethods.Contains("DELETE", StringComparer.OrdinalIgnoreCase)) ?? false));
        var endpoints = test.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.Where(x => x.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType() == typeof(CategoriesController)).ToArray();
        Assert.NotEmpty(endpoints); Assert.DoesNotContain(endpoints, x => x.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("DELETE", StringComparer.OrdinalIgnoreCase) == true);
        Assert.Equal(row, await ReadAsync(test.Factory, row.Id)); Assert.Equal(before, await DigestAsync(test.Factory));
    }

    // De helpers lezen met nieuwe scopes; de vergelijking gebruikt opgeslagen waarden en geen trackingobject.
    private static async Task LoginAsync(IdentityHttpClient browser, TestIdentity identity)
    {
        using var response = await browser.LoginAsync(identity); Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
    private static Task<HttpResponseMessage> PostAsync(IdentityHttpClient browser, string path, Dictionary<string, string> form, string token)
    {
        var posted = new List<KeyValuePair<string, string>>(form) { new("__RequestVerificationToken", token) };
        return browser.RawClient.PostAsync(path, new FormUrlEncodedContent(posted));
    }
    private static void AssertDenial(HttpResponseMessage response, string variant)
    {
        if (variant == "Pending") { Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Equal("/Account/Status", response.Headers.Location?.OriginalString); }
        else Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    private static async Task<string> DigestAsync(AccountWebApplicationFactory factory, bool includeFeatures = true)
    {
        string value = ""; await factory.WithServicesAsync(async services => value = await TrainingTestData.DigestAsync(services, includeFeatures)); return value;
    }
    private static async Task<CategoryRow> PrepareCategoryAsync(AccountWebApplicationFactory factory, string name = "Fictieve categorie")
    {
        int id = 0; await factory.WithServicesAsync(async services => id = (await TrainingTestData.CreateCategoryAsync(services, name)).Id); return await ReadAsync(factory, id);
    }
    private static async Task<CategoryRow> SingleCategoryAsync(AccountWebApplicationFactory factory)
    {
        int id = 0; await factory.WithServicesAsync(async services => id = await services.GetRequiredService<ApplicationDbContext>().Categories.Select(x => x.Id).SingleAsync()); return await ReadAsync(factory, id);
    }
    private static async Task<CategoryRow> ReadAsync(AccountWebApplicationFactory factory, int id)
    {
        CategoryRow? row = null; await factory.WithServicesAsync(async services => row = await services.GetRequiredService<ApplicationDbContext>().Categories.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new CategoryRow(x.Id, x.Name, EF.Property<string>(x, "NormalizedName"), EF.Property<Guid>(x, "Version"))).SingleAsync()); return row!;
    }
    private static void AssertFormValue(string html, string name, string expected)
    {
        foreach (Match input in Regex.Matches(html, "(?is)<input\\b[^>]*>"))
        {
            var key = Regex.Match(input.Value, "(?is)\\bname\\s*=\\s*([\"'])(.*?)\\1"); if (WebUtility.HtmlDecode(key.Groups[2].Value) != name) continue;
            var value = Regex.Match(input.Value, "(?is)\\bvalue\\s*=\\s*([\"'])(.*?)\\1"); Assert.Equal(expected, WebUtility.HtmlDecode(value.Groups[2].Value)); return;
        }
        Assert.Fail("Het verwachte categoriecontrol ontbreekt: " + name);
    }
    private sealed record CategoryRow(int Id, string Name, string NormalizedName, Guid Version);
    private sealed class Session(AccountWebApplicationFactory factory, IdentityHttpClient browser, TestIdentity admin) : IAsyncDisposable
    {
        public AccountWebApplicationFactory Factory { get; } = factory;
        public IdentityHttpClient Browser { get; } = browser;
        public TestIdentity Admin { get; } = admin;
        public static async Task<Session> CreateAsync()
        {
            var factory = new AccountWebApplicationFactory(); var admin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
            var browser = factory.CreateIdentityClient(); await LoginAsync(browser, admin); return new(factory, browser, admin);
        }
        public async ValueTask DisposeAsync() { Browser.Dispose(); await Factory.DisposeAsync(); }
    }
}
