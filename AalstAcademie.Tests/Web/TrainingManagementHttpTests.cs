// Sprint 003: Toetst de volledige opleidings-HTTP-flow, eigenaarsscope, behoud van invoer en voorwaarden, doelgroeprechten en het ontbreken van delete-, archive- en restore-acties.
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Tests.Web;

/// <summary>58 HTTP-cases gebruiken echte cookies, Razor-antiforgery en uitsluitend de eigen SQLite-testhost.</summary>
public class TrainingManagementHttpTests
{
    // TH1: de eigenaar komt uit de actuele lesgeveridentiteit of uit de expliciete beheerdersselectie.
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Successful_create_redirects_and_roundtrips_all_fields(bool external, bool administrator)
    {
        await using var test = await Session.CreateAsync(external, administrator);
        var fields = await test.CreateFormAsync();
        fields["Title"] = "  HTTP opleiding  "; fields["Description"] = "Regel één\n<script>gewone tekst</script>";
        fields["RequiresMotivation"] = "true";
        if (external) fields["ExternalTotalPriceEuros"] = "12,30";
        var identity = await DigestAsync(test.Factory, false);
        using var response = await test.Browser.PostFormAsync("/TrainingManagement/Create", fields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = await SingleTrainingAsync(test.Factory);
        Assert.Equal("HTTP opleiding", saved.Title); Assert.Equal(fields["Description"], saved.Description);
        Assert.Equal(int.Parse(fields["CategoryId"], CultureInfo.InvariantCulture), saved.CategoryId);
        Assert.True(saved.RequiresMotivation); Assert.NotEqual(Guid.Empty, saved.Version);
        Assert.Equal(external ? 12.30m : null, saved.ExternalTotalPriceEuros);
        Assert.Equal(test.Owner.UserId, saved.InstructorUserId);
        Assert.Equal(test.Owner.UserId, saved.Instructor.Id);
        Assert.Equal(TrainingAudienceScope.Unassigned, saved.AudienceScope); Assert.Empty(saved.TrainingDepartments);
        Assert.Equal(identity, await DigestAsync(test.Factory, false));
        using var details = await test.Browser.GetAsync($"/TrainingManagement/Details/{saved.Id}");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var html = await details.Content.ReadAsStringAsync();
        Assert.Contains("HTTP opleiding", html); Assert.DoesNotContain("<script>gewone tekst</script>", html);
        Assert.Contains("&lt;script&gt;", html);
        using var edit = await test.Browser.GetAsync($"/TrainingManagement/Edit/{saved.Id}");
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var editHtml = await edit.Content.ReadAsStringAsync();
        AssertFormValue(editHtml, "ExpectedVersion", saved.Version.ToString());
        foreach (var retired in new[] { "Date", "StartTime", "EndTime", "Location", "MaximumParticipants", "AudienceScope", "DepartmentIds" })
            Assert.False(HasControl(editHtml, retired));
    }

    // TH2: ook gewone HTML-POSTs behouden de bestaande login-/statusrouting bij actuele sessieverliezen.
    [Theory]
    [InlineData("anonymous")] [InlineData("Pending")] [InlineData("Refused")]
    [InlineData("blocked")] [InlineData("deleted")] [InlineData("stale")] [InlineData("demo-outside")]
    public async Task Ineligible_sessions_cannot_reach_either_feature(string variant)
    {
        await using var factory = new AccountWebApplicationFactory(demoEnabled: variant != "demo-outside");
        using var browser = factory.CreateIdentityClient();
        // Buiten demo moet de aanvankelijk geldige login ook de echte bevestigingsvoorwaarde vervullen.
        var actor = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder], emailConfirmed: true);
        var owner = await factory.AddAccountAsync(RequestedAccountType.InternalInstructor, AccountApprovalStatus.Approved, [RoleNames.Lesgever]);
        if (variant != "anonymous") await LoginAsync(browser, actor);
        var token = await browser.GetAntiforgeryTokenAsync(variant == "anonymous" ? "/Identity/Account/Login" : "/Categories/Create");
        var fields = await CreateFormAsync(factory, owner, true, false);
        if (variant != "anonymous") await factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == actor.UserId);
            if (variant is "Pending" or "Refused") user.AccountApprovalStatus = Enum.Parse<AccountApprovalStatus>(variant);
            else if (variant == "blocked") user.IsBlocked = true;
            else if (variant == "deleted") db.Users.Remove(user);
            else if (variant == "stale") Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().UpdateSecurityStampAsync(user)).Succeeded);
            else user.DemoSeedKey = "fictieve-http-demo-buiten-grens";
            await db.SaveChangesAsync();
        });
        var before = await DigestAsync(factory);
        foreach (var path in new[] { "/TrainingManagement/Create", "/Categories/Create" })
        {
            using var get = await browser.GetAsync(path); AssertSessionDenial(get, variant);
            using var post = await PostAsync(browser, path, path.StartsWith("/Categories", StringComparison.Ordinal) ? new() { ["Name"] = "Geldige naam" } : fields, token);
            AssertSessionDenial(post, variant);
        }
        Assert.Equal(before, await DigestAsync(factory));
    }

    // TH3: Approved medewerker is een geldige accountcookie maar heeft geen opleidingsbevoegdheid.
    [Fact]
    public async Task Approved_staff_receives_feature_403_for_html_get_and_post()
    {
        await using var test = await Session.CreateAsync();
        var row = await test.StoreAsync();
        var staff = await test.Factory.AddAccountAsync(RequestedAccountType.Employee, AccountApprovalStatus.Approved, [RoleNames.Medewerker]);
        using var browser = test.Factory.CreateIdentityClient(); await LoginAsync(browser, staff);
        var token = await browser.GetAntiforgeryTokenAsync("/Identity/Account/Login");
        var before = await DigestAsync(test.Factory);
        foreach (var path in new[] { "/TrainingManagement", "/TrainingManagement/Create", $"/TrainingManagement/Details/{row.Id}", $"/TrainingManagement/Edit/{row.Id}", $"/TrainingManagement/Audience/{row.Id}" })
        {
            using var response = await browser.GetAsync(path); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var post = await PostAsync(browser, "/TrainingManagement/Create", await CreateFormAsync(test.Factory, test.Owner, false, false), token);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
    }

    // TH4: een vóór de rolwijziging opgehaald formulier geeft oude cookieclaims geen blijvend recht.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Revoked_feature_role_is_rechecked_after_get_before_post(bool administrator)
    {
        await using var test = await Session.CreateAsync(administrator: administrator);
        var token = await test.Browser.GetAntiforgeryTokenAsync("/TrainingManagement/Create");
        var form = await test.CreateFormAsync();
        await test.Factory.WithServicesAsync(async services =>
        {
            var user = await services.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(x => x.Id == test.Actor.UserId);
            var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var stamp = user.SecurityStamp;
            Assert.True((await manager.RemoveFromRoleAsync(user, administrator ? RoleNames.Beheerder : RoleNames.Lesgever)).Succeeded);
            Assert.Equal(stamp, user.SecurityStamp);
        });
        var before = await DigestAsync(test.Factory);
        using var get = await test.Browser.GetAsync("/TrainingManagement/Create"); Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        using var post = await PostAsync(test.Browser, "/TrainingManagement/Create", form, token);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
    }

    // TH5: de beheerdersselector is geen snapshottoestemming voor een intussen gewijzigde eigenaar.
    [Theory] [InlineData("blocked")] [InlineData("roleless")] [InlineData("Pending")] [InlineData("profileloss")]
    public async Task Owner_eligibility_is_rechecked_after_selection_get(string variant)
    {
        await using var test = await Session.CreateAsync(administrator: true);
        var token = await test.Browser.GetAntiforgeryTokenAsync($"/TrainingManagement/Create?ownerUserId={test.Owner.UserId}");
        var fields = await test.CreateFormAsync();
        await test.Factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>(); var user = await db.Users.SingleAsync(x => x.Id == test.Owner.UserId);
            if (variant == "blocked") user.IsBlocked = true;
            else if (variant == "Pending") user.AccountApprovalStatus = AccountApprovalStatus.Pending;
            else if (variant == "roleless") Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().RemoveFromRoleAsync(user, RoleNames.Lesgever)).Succeeded);
            else db.InternalInstructors.Remove(await db.InternalInstructors.SingleAsync(x => x.ApplicationUserId == user.Id));
            await db.SaveChangesAsync();
        });
        var before = await DigestAsync(test.Factory);
        using var post = await PostAsync(test.Browser, "/TrainingManagement/Create", fields, token);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
        Assert.Contains("OwnerUserId", await post.Content.ReadAsStringAsync());
    }

    // TH6: elk verboden systeemveld wordt apart gepost, zodat één herkende sleutel de rest niet maskeert.
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Forged_fields_and_duplicate_scalar_keys_are_explicit_errors(bool administrator, bool edit)
    {
        await using var test = await Session.CreateAsync(administrator: administrator);
        var row = edit ? await test.StoreAsync() : null;
        var path = edit ? $"/TrainingManagement/Edit/{row!.Id}" : "/TrainingManagement/Create";
        var form = edit ? EditForm(row!) : await test.CreateFormAsync();
        var token = await test.Browser.GetAntiforgeryTokenAsync(path);
        var forbidden = new List<string> { "InternalInstructorId", "ExternalInstructorId", "InstructorUserId", "IsArchived", "ActorUserId", "Version", "Date", "StartTime", "EndTime", "Location", "MaximumParticipants", "AudienceScope", "DepartmentIds", "PriceSupplied", "MotivationSupplied", "ConditionsLocked" };
        if (edit || !administrator) forbidden.Add("OwnerUserId");
        var before = await DigestAsync(test.Factory);
        foreach (var key in forbidden)
        {
            var pairs = form.ToList(); pairs.Add(new(key, key == "OwnerUserId" ? test.Owner.UserId : "1"));
            using var response = await PostPairsAsync(test.Browser, path, pairs, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("validation-summary-errors", await response.Content.ReadAsStringAsync());
            Assert.Equal(before, await DigestAsync(test.Factory));
        }
        var duplicate = form.ToList(); duplicate.Add(new("Title", "Dubbele titel"));
        using var repeated = await PostPairsAsync(test.Browser, path, duplicate, token);
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
    }

    // TH7: de maker van een record krijgt na intrekking van zijn beheerrol geen eigenaarrecht.
    [Fact]
    public async Task Foreign_and_former_maker_instructors_have_no_record_rights()
    {
        foreach (var wasMaker in new[] { false, true })
        {
            await using var test = await Session.CreateAsync();
            var foreignOwner = await test.Factory.AddAccountAsync(RequestedAccountType.InternalInstructor, AccountApprovalStatus.Approved, [RoleNames.Lesgever]);
            if (wasMaker) await ChangeRoleAsync(test.Factory, test.Owner.UserId, RoleNames.Beheerder, true);
            TrainingEntity? row = null;
            await test.Factory.WithServicesAsync(async services =>
            {
                var db = services.GetRequiredService<ApplicationDbContext>();
                row = await TrainingTestData.CreateTrainingAsync(services, await db.Users.SingleAsync(x => x.Id == foreignOwner.UserId),
                    await db.Users.SingleAsync(x => x.Id == (wasMaker ? test.Owner.UserId : test.Admin.UserId)));
            });
            if (wasMaker) await ChangeRoleAsync(test.Factory, test.Owner.UserId, RoleNames.Beheerder, false);
            var token = await test.Browser.GetAntiforgeryTokenAsync("/TrainingManagement/Create"); var before = await DigestAsync(test.Factory);
            foreach (var action in new[] { "Details", "Edit" })
            {
                using var get = await test.Browser.GetAsync($"/TrainingManagement/{action}/{row!.Id}"); Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
                if (action == "Details") continue;
                using var post = await PostAsync(test.Browser, $"/TrainingManagement/{action}/{row.Id}", action == "Edit" ? EditForm(row) : VersionForm(row), token);
                Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
            }
            Assert.Equal(before, await DigestAsync(test.Factory));
        }
    }

    // TH8: actuele metadata, vaste UserID-scope en Title/Id-volgorde komen uit providerreads, met pagina's van twintig.
    [Fact]
    public async Task Listing_is_scoped_paged_filtered_and_encodes_current_labels()
    {
        await using var test = await Session.CreateAsync();
        var ownIds = new List<int>(); var categoryIds = new List<int>();
        var ids = await DepartmentIdsAsync(test.Factory);
        await test.Factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var owner = await db.Users.SingleAsync(x => x.Id == test.Owner.UserId);
            var admin = await db.Users.SingleAsync(x => x.Id == test.Admin.UserId);
            for (var index = 0; index < 21; index++)
            {
                var row = await TrainingTestData.CreateTrainingAsync(services, owner, admin,
                    index == 0 ? TrainingAudienceScope.AllDepartments : index == 1 ? TrainingAudienceScope.SelectedDepartments : TrainingAudienceScope.Unassigned,
                    index == 1 ? [ids[0]] : []);
                var tracked = await db.Trainings.SingleAsync(x => x.Id == row.Id);
                tracked.Title = "HTTP <opleiding>"; tracked.Version = Guid.NewGuid();
                await db.SaveChangesAsync(); ownIds.Add(row.Id); categoryIds.Add(row.CategoryId);
            }
        });
        var foreignOwner = await test.Factory.AddAccountAsync(RequestedAccountType.InternalInstructor, AccountApprovalStatus.Approved, [RoleNames.Lesgever]);
        TrainingEntity? foreign = null;
        await test.Factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            foreign = await TrainingTestData.CreateTrainingAsync(services, await db.Users.SingleAsync(x => x.Id == foreignOwner.UserId));
            var tracked = await db.Trainings.SingleAsync(x => x.Id == foreign.Id); tracked.Title = "Privéopleiding andere eigenaar";
            await db.SaveChangesAsync();
        });
        await test.Factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            // De labels wijzigen na alle fixtureaanmaak: de lijst moet actuele providerdata tonen, geen aanmaaksnapshot.
            var currentOwner = await db.Users.SingleAsync(x => x.Id == test.Owner.UserId);
            currentOwner.FirstName = "Noor <owner>"; currentOwner.LastName = "Actueel";
            await db.SaveChangesAsync();
            var version = await db.Categories.Where(x => x.Id == categoryIds[0]).Select(x => EF.Property<Guid>(x, "Version")).SingleAsync();
            Assert.True((await services.GetRequiredService<CategoryManagementService>().RenameAsync(test.Admin.UserId,
                new(categoryIds[0], version, "Actuele <categorie>"))).Succeeded);
        });
        var before = await DigestAsync(test.Factory);
        using var first = await test.Browser.GetAsync("/TrainingManagement"); Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstHtml = await first.Content.ReadAsStringAsync();
        Assert.Equal(ownIds.Take(20), DetailIds(firstHtml)); Assert.DoesNotContain("Privéopleiding andere eigenaar", firstHtml);
        // De lesgever ziet zijn eigen titel/categorie; de eigenaarlabel behoort uitsluitend tot het beheerdersoverzicht.
        var labelledRow = Assert.Single(Regex.Matches(firstHtml, "(?is)<tr\\b[^>]*>.*?</tr>").Cast<Match>(),
            match => DetailIds(match.Value).Contains(ownIds[0])).Value;
        Assert.Contains("HTTP &lt;opleiding&gt;", labelledRow); Assert.DoesNotContain("Noor &lt;owner&gt; Actueel", labelledRow);
        Assert.Contains("Actuele &lt;categorie&gt;", labelledRow);
        Assert.DoesNotContain("HTTP <opleiding>", firstHtml); Assert.DoesNotContain("Noor <owner>", firstHtml);
        Assert.DoesNotContain("Actuele <categorie>", firstHtml);
        using var second = await test.Browser.GetAsync("/TrainingManagement?page=2"); Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(ownIds.Skip(20), DetailIds(await second.Content.ReadAsStringAsync()));
        // Elke filter gebruikt de categorie van zijn eigen fixture, ook wanneer de aanmaakhelper nieuwe categorieën toevoegt.
        foreach (var filter in new[] { (Scope: "allDepartments", Id: ownIds[0], CategoryId: categoryIds[0]), (Scope: "selectedDepartments", Id: ownIds[1], CategoryId: categoryIds[1]) })
        {
            using var filtered = await test.Browser.GetAsync($"/TrainingManagement?categoryId={filter.CategoryId}&audience={filter.Scope}");
            Assert.Equal(HttpStatusCode.OK, filtered.StatusCode); Assert.Equal(new[] { filter.Id }, DetailIds(await filtered.Content.ReadAsStringAsync()));
        }
        using var adminBrowser = test.Factory.CreateIdentityClient(); await LoginAsync(adminBrowser, test.Admin);
        // Een aparte echte beheerderscookie toont op pagina één dezelfde bestaande rij mét actuele, veilig geëncodeerde eigenaarlabel.
        using var adminFirst = await adminBrowser.GetAsync("/TrainingManagement"); Assert.Equal(HttpStatusCode.OK, adminFirst.StatusCode);
        var adminFirstHtml = await adminFirst.Content.ReadAsStringAsync();
        Assert.Equal(ownIds.Take(20), DetailIds(adminFirstHtml));
        var adminLabelledRow = Assert.Single(Regex.Matches(adminFirstHtml, "(?is)<tr\\b[^>]*>.*?</tr>").Cast<Match>(),
            match => DetailIds(match.Value).Contains(ownIds[0])).Value;
        Assert.Contains("HTTP &lt;opleiding&gt;", adminLabelledRow); Assert.Contains("Noor &lt;owner&gt; Actueel", adminLabelledRow);
        Assert.Contains("Actuele &lt;categorie&gt;", adminLabelledRow);
        Assert.DoesNotContain("HTTP <opleiding>", adminFirstHtml); Assert.DoesNotContain("Noor <owner>", adminFirstHtml);
        Assert.DoesNotContain("Actuele <categorie>", adminFirstHtml);
        using var adminPage = await adminBrowser.GetAsync("/TrainingManagement?page=2");
        Assert.Equal(HttpStatusCode.OK, adminPage.StatusCode);
        Assert.Contains(foreign!.Id, DetailIds(await adminPage.Content.ReadAsStringAsync()));
        Assert.Equal(before, await DigestAsync(test.Factory));
    }

    // TH9: vijf raw-invoergroepen bewaken malformed scalars en filters, zonder binderdefault of afgeronde prijs.
    [Theory]
    [InlineData("category")]
    [InlineData("money")]
    [InlineData("owner")]
    [InlineData("AudienceScope")]
    [InlineData("DepartmentIds")]
    public async Task Malformed_metadata_owner_and_audience_values_are_400_without_writes(string variant)
    {
        await using var test = await Session.CreateAsync(external: true, administrator: true);
        var row = variant is "AudienceScope" or "DepartmentIds" ? await test.StoreAsync() : null;
        var path = row is null ? "/TrainingManagement/Create" : $"/TrainingManagement/Audience/{row.Id}";
        var form = row is null ? await test.CreateFormAsync() : AudienceForm(row, TrainingAudienceScope.SelectedDepartments);
        var key = variant switch { "category" => "CategoryId", "money" => "ExternalTotalPriceEuros", "owner" => "OwnerUserId", _ => variant };
        form[key] = variant switch { "category" => "1.5", "money" => "1e2", "owner" => "ontbrekende-eigenaar", "AudienceScope" => "999", _ => "niet-een-int" };
        var before = await DigestAsync(test.Factory);
        using var response = await test.Browser.PostFormAsync(path, form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
        Assert.Contains("validation-summary-errors", await response.Content.ReadAsStringAsync());
        if (variant == "category")
        {
            foreach (var query in new[] { "categoryId=1.5", "categoryId=2147483648", "categoryId=0", "categoryId=-1", "page=1.5", "page=0", "page=2147483648", "audience=999" })
            {
                using var badFilter = await test.Browser.GetAsync("/TrainingManagement?" + query);
                Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
            }
            foreach (var omitted in new[] { "Title", "CategoryId" })
            {
                var missing = await test.CreateFormAsync(); missing.Remove(omitted);
                using var badMissing = await test.Browser.PostFormAsync("/TrainingManagement/Create", missing);
                Assert.Equal(HttpStatusCode.BadRequest, badMissing.StatusCode);
            }
        }
        Assert.Equal(before, await DigestAsync(test.Factory));
    }

    // TH10: losse scalarvelden mogen niet meerdere waarden hebben; alleen de gewone checkbox true/false-paarvorm is geldig.
    [Fact]
    public async Task Scalar_multiplicity_and_checkbox_shape_are_explicit()
    {
        await using var test = await Session.CreateAsync(external: true, administrator: true);
        var form = await test.CreateFormAsync(); var token = await test.Browser.GetAntiforgeryTokenAsync("/TrainingManagement/Create");
        var before = await DigestAsync(test.Factory);
        foreach (var key in new[] { "CategoryId", "OwnerUserId", "ExternalTotalPriceEuros", "RequiresMotivation" })
        {
            var duplicate = form.ToList(); duplicate.Add(new(key, form[key]));
            using var rejected = await PostPairsAsync(test.Browser, "/TrainingManagement/Create", duplicate, token);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
        }
        foreach (var value in new[] { "yes", "1", "", "TRUE,FALSE", "maybe" })
        {
            var invalid = new Dictionary<string, string>(form) { ["RequiresMotivation"] = value };
            using var rejected = await PostAsync(test.Browser, "/TrainingManagement/Create", invalid, token);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
        }
        var checkbox = form.Where(x => x.Key != "RequiresMotivation").ToList();
        checkbox.Add(new("RequiresMotivation", "true")); checkbox.Add(new("RequiresMotivation", "false"));
        using var accepted = await PostPairsAsync(test.Browser, "/TrainingManagement/Create", checkbox, token);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var row = await SingleTrainingAsync(test.Factory); Assert.True(row.RequiresMotivation);

        // Een onparsebare checkboxwaarde moet ook bij Edit veilig herweergeven, met het oorspronkelijke versietoken.
        var editPath = $"/TrainingManagement/Edit/{row.Id}";
        var editToken = await test.Browser.GetAntiforgeryTokenAsync(editPath);
        var editBefore = await DigestAsync(test.Factory);
        var invalidEdit = EditForm(row); invalidEdit["RequiresMotivation"] = "maybe";
        using var invalidEditResponse = await PostAsync(test.Browser, editPath, invalidEdit, editToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidEditResponse.StatusCode);
        Assert.Equal(editBefore, await DigestAsync(test.Factory));
        var invalidEditHtml = await invalidEditResponse.Content.ReadAsStringAsync();
        Assert.Contains("validation-summary-errors", invalidEditHtml);
        AssertFormValue(invalidEditHtml, "ExpectedVersion", row.Version.ToString());

        // Een geldig cookie en antiforgeryheader maken JSON nog geen formulier: afwijzen vóór ReadFormAsync en elke write.
        using var jsonRequest = new HttpRequestMessage(HttpMethod.Post, editPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(EditForm(row)), Encoding.UTF8, "application/json")
        };
        jsonRequest.Headers.Add("RequestVerificationToken", editToken);
        using var jsonResponse = await test.Browser.RawClient.SendAsync(jsonRequest);
        Assert.Equal(HttpStatusCode.BadRequest, jsonResponse.StatusCode);
        Assert.Equal(editBefore, await DigestAsync(test.Factory));
    }

    // TH11: foutrespons bewaart de veilige geposte tekst en oorspronkelijke versie, zonder HTML uit te voeren.
    [Fact]
    public async Task Field_errors_encode_description_and_preserve_raw_input_and_version()
    {
        await using var test = await Session.CreateAsync(); var row = await test.StoreAsync();
        var form = EditForm(row); form["Title"] = "  ";
        form["Description"] = "<script>alert('fictief')</script>\nTweede regel";
        var before = await DigestAsync(test.Factory);
        using var response = await test.Browser.PostFormAsync($"/TrainingManagement/Edit/{row.Id}", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
        var html = await response.Content.ReadAsStringAsync();
        AssertFormValue(html, "Title", form["Title"]);
        AssertFormValue(html, "ExpectedVersion", row.Version.ToString()); AssertFormValue(html, "Description", form["Description"]);
        Assert.DoesNotContain("<script>alert('fictief')</script>", html); Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("validation-summary-errors", html);
    }

    // TH12: vier onbruikbare wiretokens zijn400; alleen een werkelijk eerder gelezen, intussen gewijzigde Guid is409.
    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("empty-guid")]
    [InlineData("malformed")]
    [InlineData("stale")]
    public async Task Version_input_is_strict_and_never_refreshed_after_rejection(string variant)
    {
        await using var test = await Session.CreateAsync(administrator: true); var row = await test.StoreAsync();
        if (variant == "stale")
        {
            await test.Factory.WithServicesAsync(async services => Assert.True((await services.GetRequiredService<TrainingManagementService>()
                .UpdateAsync(test.Admin.UserId, new(row.Id, row.Version, Fields(row) with { Title = "Onafhankelijk bijgewerkt" }))).Succeeded));
            Assert.NotEqual(row.Version, (await ReadAsync(test.Factory, row.Id)).Version);
        }
        var version = variant switch { "missing" => null, "blank" => "", "empty-guid" => Guid.Empty.ToString(), "malformed" => "geen-guid", _ => row.Version.ToString() };
        foreach (var action in new[] { "Edit", "Audience" })
        {
            var form = action == "Edit" ? EditForm(row) : AudienceForm(row, TrainingAudienceScope.AllDepartments);
            if (version is null) form.Remove("ExpectedVersion"); else form["ExpectedVersion"] = version;
            if (action == "Edit") form["Title"] = "Mijn open formulier";
            var before = await DigestAsync(test.Factory);
            using var response = await test.Browser.PostFormAsync($"/TrainingManagement/{action}/{row.Id}", form);
            Assert.Equal(variant == "stale" ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(before, await DigestAsync(test.Factory));
            var html = await response.Content.ReadAsStringAsync(); AssertFormValue(html, "ExpectedVersion", version ?? "");
            if (action == "Edit") AssertFormValue(html, "Title", form["Title"]);
            if (variant == "stale") AssertReloadAnchor(html, $"/TrainingManagement/{action}/{row.Id}");
        }
    }

    // TH13: interne/externe historie verhindert geen gedeelde titel/beschrijving/categorie-edit door de eigen lesgever.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Historical_shared_metadata_remains_editable(bool external)
    {
        await using var test = await Session.CreateAsync(external);
        var row = await test.StoreAsync(); await AddHistoryAsync(test, row, "complete");
        var foundation = await FoundationDigestAsync(test.Factory); var identity = await DigestAsync(test.Factory, false);
        var form = EditForm(row); form["Title"] = "Actuele historische titel"; form["Description"] = "Actuele <metadata>";
        form.Remove("ExternalTotalPriceEuros"); form.Remove("RequiresMotivation");
        await test.Factory.WithServicesAsync(async services => form["CategoryId"] = (await TrainingTestData.CreateCategoryAsync(services, "Nieuwe historische categorie")).Id.ToString(CultureInfo.InvariantCulture));
        using var edit = await test.Browser.GetAsync($"/TrainingManagement/Edit/{row.Id}"); Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        using var response = await test.Browser.PostFormAsync($"/TrainingManagement/Edit/{row.Id}", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = await ReadAsync(test.Factory, row.Id);
        Assert.Equal(form["Title"], saved.Title); Assert.Equal(form["Description"], saved.Description);
        Assert.Equal(int.Parse(form["CategoryId"], CultureInfo.InvariantCulture), saved.CategoryId);
        Assert.Equal(row.ExternalTotalPriceEuros, saved.ExternalTotalPriceEuros); Assert.Equal(row.RequiresMotivation, saved.RequiresMotivation);
        Assert.Equal(row.InstructorUserId, saved.InstructorUserId); Assert.NotEqual(row.Version, saved.Version);
        Assert.Equal(foundation, await FoundationDigestAsync(test.Factory)); Assert.Equal(identity, await DigestAsync(test.Factory, false));
    }

    // TH14: iedere registratietoestand en beide bufferhistorievormen blokkeren uitsluitend werkelijk gewijzigde voorwaarden.
    [Theory]
    [InlineData("Requested")] [InlineData("Confirmed")] [InlineData("Refused")] [InlineData("Cancelled")]
    [InlineData("active-buffer")] [InlineData("closed-buffer")]
    public async Task Any_history_after_get_blocks_actual_terms_changes(string kind)
    {
        await using var test = await Session.CreateAsync(external: true, administrator: true);
        var row = await test.StoreAsync(); var path = $"/TrainingManagement/Edit/{row.Id}";
        var token = await test.Browser.GetAntiforgeryTokenAsync(path);
        await AddHistoryAsync(test, row, kind); var before = await DigestAsync(test.Factory);
        foreach (var field in new[] { "ExternalTotalPriceEuros", "RequiresMotivation" })
        {
            var form = EditForm(row); form["Title"] = "Mijn nog open formulier";
            form[field] = field == "ExternalTotalPriceEuros" ? "25.00" : "true";
            using var rejected = await PostAsync(test.Browser, path, form, token);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
            var html = await rejected.Content.ReadAsStringAsync();
            AssertFormValue(html, "ExpectedVersion", row.Version.ToString()); AssertFormValue(html, "Title", form["Title"]); AssertReloadAnchor(html, path);
        }
    }

    // TH15: ontbrekende readonlyvelden bewaren opgeslagen prijs/true; expliciet dezelfde voorwaarden blijven toegestaan.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Omitted_or_explicit_unchanged_locked_terms_preserve_stored_values(bool supplied)
    {
        await using var test = await Session.CreateAsync(external: true, administrator: true);
        var row = await test.StoreAsync();
        await test.Factory.WithServicesAsync(async services => Assert.True((await services.GetRequiredService<TrainingManagementService>()
            .UpdateAsync(test.Admin.UserId, new(row.Id, row.Version, Fields(row) with { RequiresMotivation = true }))).Succeeded));
        row = await ReadAsync(test.Factory, row.Id); await AddHistoryAsync(test, row, "complete");
        var foundation = await FoundationDigestAsync(test.Factory); var identity = await DigestAsync(test.Factory, false);
        var path = $"/TrainingManagement/Edit/{row.Id}";
        using var get = await test.Browser.GetAsync(path); Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var html = await get.Content.ReadAsStringAsync();
        Assert.False(HasControl(html, "ExternalTotalPriceEuros")); Assert.False(HasControl(html, "RequiresMotivation"));
        var form = EditForm(row); form["Title"] = "Metadata met vaste voorwaarden";
        if (!supplied) { form.Remove("ExternalTotalPriceEuros"); form.Remove("RequiresMotivation"); }
        using var response = await test.Browser.PostFormAsync(path, form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = await ReadAsync(test.Factory, row.Id); Assert.Equal(form["Title"], saved.Title);
        Assert.Equal(row.ExternalTotalPriceEuros, saved.ExternalTotalPriceEuros); Assert.True(saved.RequiresMotivation);
        Assert.Equal(row.InstructorUserId, saved.InstructorUserId); Assert.NotEqual(row.Version, saved.Version);
        Assert.Equal(foundation, await FoundationDigestAsync(test.Factory)); Assert.Equal(identity, await DigestAsync(test.Factory, false));
        if (supplied)
        {
            // Aanwezige maar lege externeprijs is een veldfout, geen omission die de oude prijs mag herstellen.
            var empty = EditForm(saved); empty["ExternalTotalPriceEuros"] = ""; var before = await DigestAsync(test.Factory);
            using var invalid = await test.Browser.PostFormAsync(path, empty); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal(before, await DigestAsync(test.Factory));
        }
    }

    // TH16: bestaande records blijven beheersbaar zonder accountrollen, profielen of Identity-stamps te herstellen.
    [Theory] [InlineData("blocked")] [InlineData("roleless")]
    public async Task Administrator_manages_existing_ineligible_owner_without_identity_repair(string variant)
    {
        await using var test = await Session.CreateAsync(administrator: true); var row = await test.StoreAsync();
        await test.Factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>(); var user = await db.Users.SingleAsync(x => x.Id == test.Owner.UserId);
            if (variant == "blocked") user.IsBlocked = true;
            else Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().RemoveFromRoleAsync(user, RoleNames.Lesgever)).Succeeded);
            await db.SaveChangesAsync();
        });
        var identity = await DigestAsync(test.Factory, false);
        using var details = await test.Browser.GetAsync($"/TrainingManagement/Details/{row.Id}"); Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var edit = EditForm(row); edit["Title"] = "Beheer blijft mogelijk";
        using var updated = await test.Browser.PostFormAsync($"/TrainingManagement/Edit/{row.Id}", edit); Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        row = await ReadAsync(test.Factory, row.Id);
        using var audience = await test.Browser.PostFormAsync($"/TrainingManagement/Audience/{row.Id}", AudienceForm(row, TrainingAudienceScope.AllDepartments)); Assert.Equal(HttpStatusCode.Redirect, audience.StatusCode);
        row = await ReadAsync(test.Factory, row.Id); Assert.Equal(TrainingAudienceScope.AllDepartments, row.AudienceScope);
        Assert.Equal(test.Owner.UserId, row.InstructorUserId); Assert.Equal(identity, await DigestAsync(test.Factory, false));
    }

    // TH17: dezelfde geldige cookie onderscheidt private records404 van een beheerdersactie403, zonder AJAX.
    [Fact]
    public async Task Foreign_record_and_wrong_feature_role_do_not_leak_private_field_errors()
    {
        await using var test = await Session.CreateAsync();
        var foreign = await test.Factory.AddAccountAsync(RequestedAccountType.ExternalInstructor, AccountApprovalStatus.Approved, [RoleNames.Lesgever]);
        TrainingEntity? row = null;
        await test.Factory.WithServicesAsync(async services => row = await TrainingTestData.CreateTrainingAsync(services,
            await services.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(x => x.Id == foreign.UserId)));
        var own = await test.StoreAsync();
        var token = await test.Browser.GetAntiforgeryTokenAsync("/TrainingManagement/Create"); var before = await DigestAsync(test.Factory);
        foreach (var id in new[] { row!.Id, int.MaxValue })
        {
            using var details = await test.Browser.GetAsync($"/TrainingManagement/Details/{id}"); Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
            using var edit = await PostAsync(test.Browser, $"/TrainingManagement/Edit/{id}", new() { ["Title"] = "" }, token);
            Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode); Assert.DoesNotContain(row.Title, await edit.Content.ReadAsStringAsync());
            using var audience = await test.Browser.GetAsync($"/TrainingManagement/Audience/{id}"); Assert.Equal(HttpStatusCode.Forbidden, audience.StatusCode);
            using var post = await PostAsync(test.Browser, $"/TrainingManagement/Audience/{id}", AudienceForm(row, TrainingAudienceScope.AllDepartments), token);
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        }
        using var ownGet = await test.Browser.GetAsync($"/TrainingManagement/Audience/{own.Id}"); Assert.Equal(HttpStatusCode.Forbidden, ownGet.StatusCode);
        using var ownPost = await PostAsync(test.Browser, $"/TrainingManagement/Audience/{own.Id}", AudienceForm(own, TrainingAudienceScope.AllDepartments), token); Assert.Equal(HttpStatusCode.Forbidden, ownPost.StatusCode);
        Assert.Equal(before, await DigestAsync(test.Factory));
    }

    // TH18: alle huidige afdelingen selecteren blijft Selected; duplicates, ongeldige links en unassign zijn fouten.
    [Fact]
    public async Task Audience_http_keeps_all_and_selected_semantics_and_rejects_invalid_sets()
    {
        await using var test = await Session.CreateAsync(administrator: true); var row = await test.StoreAsync();
        await test.Factory.WithServicesAsync(async services =>
        {
            var moment = await TrainingTestData.CreateMomentAsync(services, row, date: new DateOnly(2026, 10, 1));
            await TrainingTestData.CreateRegistrationAsync(services, moment, RegistrationStatus.Cancelled);
            await TrainingTestData.CreateWaitlistEntryAsync(services, row, closed: true);
        });
        var foundation = await FoundationDigestAsync(test.Factory);
        var ids = await DepartmentIdsAsync(test.Factory); var path = $"/TrainingManagement/Audience/{row.Id}";
        var token = await test.Browser.GetAntiforgeryTokenAsync(path);
        using var selected = await PostPairsAsync(test.Browser, path, AudiencePairs(row, TrainingAudienceScope.SelectedDepartments, ids), token);
        Assert.Equal(HttpStatusCode.Redirect, selected.StatusCode); row = await ReadAsync(test.Factory, row.Id);
        Assert.Equal(TrainingAudienceScope.SelectedDepartments, row.AudienceScope); Assert.Equal(ids, row.TrainingDepartments.Select(x => x.DepartmentId).Order());
        await test.Factory.WithServicesAsync(async services => { var db = services.GetRequiredService<ApplicationDbContext>(); db.Departments.Add(new() { Name = "Fictieve latere HTTP afdeling" }); await db.SaveChangesAsync(); });
        Assert.Equal(ids.Length, (await ReadAsync(test.Factory, row.Id)).TrainingDepartments.Count);
        foreach (var pairs in new[] { AudiencePairs(row, TrainingAudienceScope.SelectedDepartments, []), AudiencePairs(row, TrainingAudienceScope.SelectedDepartments, [ids[0], ids[0]]), AudiencePairs(row, TrainingAudienceScope.SelectedDepartments, [int.MaxValue]), AudiencePairs(row, TrainingAudienceScope.AllDepartments, [ids[0]]), AudiencePairs(row, TrainingAudienceScope.Unassigned, []) })
        {
            var before = await DigestAsync(test.Factory); using var rejected = await PostPairsAsync(test.Browser, path, pairs, token);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
        }
        using var all = await PostPairsAsync(test.Browser, path, AudiencePairs(row, TrainingAudienceScope.AllDepartments, []), token); Assert.Equal(HttpStatusCode.Redirect, all.StatusCode);
        row = await ReadAsync(test.Factory, row.Id); Assert.Equal(TrainingAudienceScope.AllDepartments, row.AudienceScope); Assert.Empty(row.TrainingDepartments);
        Assert.Equal(foundation, await FoundationDigestAsync(test.Factory));
    }

    // TH19: geldige bevoegdheid en businessinput zijn onvoldoende zonder het echte antiforgery-token.
    [Theory] [InlineData("Create")] [InlineData("Edit")] [InlineData("Audience")]
    public async Task Every_training_mutation_requires_antiforgery(string action)
    {
        await using var test = await Session.CreateAsync(administrator: true); var row = await test.StoreAsync();
        var form = action == "Create" ? await test.CreateFormAsync() : action == "Edit" ? EditForm(row) : action == "Audience" ? AudienceForm(row, TrainingAudienceScope.AllDepartments) : VersionForm(row);
        var path = action == "Create" ? "/TrainingManagement/Create" : $"/TrainingManagement/{action}/{row.Id}";
        var before = await DigestAsync(test.Factory);
        using var response = await test.Browser.RawClient.PostAsync(path, new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(before, await DigestAsync(test.Factory));
    }

    // TH20: één host per GUID-doel; iedere late fout volgt echte SQL, volledige rollback en een retry met dezelfde token.
    [Theory] [InlineData("parent")] [InlineData("audience-link")]
    public async Task Late_sql_failure_returns_503_with_rollback_and_clean_retry(string group)
    {
        foreach (var point in group == "parent" ? new[] { "create-parent", "update-parent" } : new[] { "audience-link" })
        {
            var fault = new TrainingFailureInterceptor();
            await using var test = await Session.CreateAsync(administrator: true, fault: fault);
            var ids = await DepartmentIdsAsync(test.Factory);
            var row = point == "create-parent" ? null : await test.StoreAsync(
                point == "audience-link" ? TrainingAudienceScope.SelectedDepartments : TrainingAudienceScope.Unassigned,
                point == "audience-link" ? [ids[0]] : []);
            var path = row is null ? "/TrainingManagement/Create" : $"/TrainingManagement/{(point == "update-parent" ? "Edit" : "Audience")}/{row.Id}";
            var pairs = row is null ? (await test.CreateFormAsync()).ToList() : point == "update-parent" ? EditForm(row).ToList()
                : AudiencePairs(row, TrainingAudienceScope.SelectedDepartments, [ids[1]]);
            if (point == "update-parent") pairs = pairs.Select(x => x.Key == "Title" ? new KeyValuePair<string, string>(x.Key, "Titel na foutretry") : x).ToList();
            var token = await test.Browser.GetAntiforgeryTokenAsync(path);
            var before = await DigestAsync(test.Factory); var identity = await DigestAsync(test.Factory, false); fault.Arm(point);
            using var response = await PostPairsAsync(test.Browser, path, pairs, token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(fault.Triggered); Assert.True(fault.SawSqlWrite); Assert.True(fault.SawParentWrite);
            Assert.Equal(before, await DigestAsync(test.Factory));
            var html = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("Fictieve fout na echte SQL-write", html);
            if (row is not null) AssertFormValue(html, "ExpectedVersion", row.Version.ToString());
            fault.Disarm();
            using var retry = await PostPairsAsync(test.Browser, path, pairs, token); Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
            var saved = row is null ? await SingleTrainingAsync(test.Factory) : await ReadAsync(test.Factory, row.Id);
            Assert.Equal(test.Owner.UserId, saved.InstructorUserId);
            if (row is null) { Assert.Equal(TrainingAudienceScope.Unassigned, saved.AudienceScope); Assert.Empty(saved.TrainingDepartments); }
            else
            {
                Assert.NotEqual(row.Version, saved.Version);
                if (point == "update-parent") Assert.Equal("Titel na foutretry", saved.Title);
                else { Assert.Equal(TrainingAudienceScope.SelectedDepartments, saved.AudienceScope); Assert.Equal(new[] { ids[1] }, saved.TrainingDepartments.Select(x => x.DepartmentId).Order()); }
            }
            Assert.Equal(identity, await DigestAsync(test.Factory, false));
        }
    }

    // TH21: prijsdeel en eigenaarselectie ontstaan via gewone servernavigatie, zonder JavaScript-uitvoering.
    [Fact]
    public async Task Administrator_owner_selection_changes_price_form_through_plain_get()
    {
        await using var test = await Session.CreateAsync(administrator: true);
        var external = await test.Factory.AddAccountAsync(RequestedAccountType.ExternalInstructor, AccountApprovalStatus.Approved, [RoleNames.Lesgever]);
        // Zonder categorie toont de server uitleg en uitsluitend de beheerder een pad naar categorie-aanmaak.
        var emptyDigest = await DigestAsync(test.Factory);
        using var emptyAdmin = await test.Browser.GetAsync("/TrainingManagement/Create"); Assert.Equal(HttpStatusCode.OK, emptyAdmin.StatusCode);
        var emptyAdminHtml = await emptyAdmin.Content.ReadAsStringAsync(); AssertNoCreatePostForm(emptyAdminHtml); AssertReloadAnchor(emptyAdminHtml, "/Categories/Create");
        using (var instructor = test.Factory.CreateIdentityClient())
        {
            await LoginAsync(instructor, test.Owner);
            using var emptyInstructor = await instructor.GetAsync("/TrainingManagement/Create"); Assert.Equal(HttpStatusCode.OK, emptyInstructor.StatusCode);
            var emptyInstructorHtml = await emptyInstructor.Content.ReadAsStringAsync(); AssertNoCreatePostForm(emptyInstructorHtml);
            Assert.Contains("categorie", emptyInstructorHtml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href=\"/Categories/Create\"", emptyInstructorHtml);
        }
        Assert.Equal(emptyDigest, await DigestAsync(test.Factory));
        await test.Factory.WithServicesAsync(async services => { await TrainingTestData.CreateCategoryAsync(services); });
        using var initial = await test.Browser.GetAsync("/TrainingManagement/Create"); Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var initialHtml = await initial.Content.ReadAsStringAsync();
        Assert.Matches("(?is)<form\\b[^>]*method=[\"']get[\"']", initialHtml); Assert.Contains("name=\"ownerUserId\"", initialHtml);
        using var externalPage = await test.Browser.GetAsync($"/TrainingManagement/Create?ownerUserId={external.UserId}"); Assert.Equal(HttpStatusCode.OK, externalPage.StatusCode);
        var externalHtml = await externalPage.Content.ReadAsStringAsync(); AssertFormValue(externalHtml, "OwnerUserId", external.UserId);
        Assert.Contains("name=\"ExternalTotalPriceEuros\"", externalHtml); Assert.DoesNotContain("data-registration-form", externalHtml);
        using var internalPage = await test.Browser.GetAsync($"/TrainingManagement/Create?ownerUserId={test.Owner.UserId}"); Assert.Equal(HttpStatusCode.OK, internalPage.StatusCode);
        var internalHtml = await internalPage.Content.ReadAsStringAsync(); AssertFormValue(internalHtml, "OwnerUserId", test.Owner.UserId);
        Assert.DoesNotContain("name=\"ExternalTotalPriceEuros\"", internalHtml);
        var form = await CreateFormAsync(test.Factory, external, true, true); form["ExternalTotalPriceEuros"] = "0";
        using var created = await test.Browser.PostFormAsync("/TrainingManagement/Create", form, $"/TrainingManagement/Create?ownerUserId={external.UserId}"); Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal(external.UserId, (await SingleTrainingAsync(test.Factory)).InstructorUserId);
    }

    // TH22: geldige admincookie/CSRF plus route- en actiemetadata bewijzen dat retired acties niet bestaan.
    [Fact]
    public async Task Archive_restore_and_training_delete_are_absent_for_all_methods()
    {
        await using var test = await Session.CreateAsync(administrator: true); var row = await test.StoreAsync();
        await AddHistoryAsync(test, row, "complete");
        var token = await test.Browser.GetAntiforgeryTokenAsync("/TrainingManagement/Create"); var before = await DigestAsync(test.Factory);
        foreach (var action in new[] { "Archive", "Restore", "Delete" })
        {
            var path = $"/TrainingManagement/{action}/{row.Id}";
            using var get = await test.Browser.GetAsync(path); Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            using var post = await PostAsync(test.Browser, path, VersionForm(row), token); Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
            using var request = new HttpRequestMessage(HttpMethod.Delete, path) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }) };
            using var deleted = await test.Browser.RawClient.SendAsync(request); Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }
        var actions = test.Factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
            .Where(x => x.ControllerTypeInfo.AsType() == typeof(TrainingManagementController)).ToArray();
        Assert.NotEmpty(actions);
        Assert.DoesNotContain(actions, x => new[] { "Archive", "Restore", "Delete", "Remove" }.Any(name => x.ActionName.Contains(name, StringComparison.OrdinalIgnoreCase))
            || (x.ActionConstraints?.OfType<HttpMethodActionConstraint>().Any(c => c.HttpMethods.Contains("DELETE", StringComparer.OrdinalIgnoreCase)) ?? false));
        var endpoints = test.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(x => x.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType() == typeof(TrainingManagementController)).ToArray();
        Assert.NotEmpty(endpoints); Assert.DoesNotContain(endpoints, x => x.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("DELETE", StringComparer.OrdinalIgnoreCase) == true);
        Assert.Equal(before, await DigestAsync(test.Factory)); Assert.Equal(row.Version, (await ReadAsync(test.Factory, row.Id)).Version);
    }

    // TH23: een oude archivequery wordt zichtbaar afgewezen en niet stil als een andere lijstfilter toegepast.
    [Fact]
    public async Task Legacy_archive_filter_is_400_for_every_previous_value()
    {
        await using var test = await Session.CreateAsync(); await test.StoreAsync(); var before = await DigestAsync(test.Factory);
        foreach (var value in new[] { "active", "archived", "all", "unknown" })
        {
            using var response = await test.Browser.GetAsync("/TrainingManagement?archive=" + value);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(before, await DigestAsync(test.Factory));
    }

    // Deze helpers bereiden fictieve data voor; iedere opslagcontrole opent een onafhankelijke DI-scope.
    /// <summary>Echte fixturehistorie wordt vóór de HTTP-write gecommit; de cookieklok blijft binnen dezelfde sessie.</summary>
    private static async Task AddHistoryAsync(Session test, TrainingEntity row, string kind)
    {
        await test.Factory.WithServicesAsync(async services =>
        {
            if (kind is "active-buffer" or "closed-buffer")
                await TrainingTestData.CreateWaitlistEntryAsync(services, row, closed: kind == "closed-buffer");
            else
            {
                var moment = await TrainingTestData.CreateMomentAsync(services, row, date: new DateOnly(2026, 10, 1));
                await TrainingTestData.CreateRegistrationAsync(services, moment,
                    kind == "complete" ? RegistrationStatus.Refused : Enum.Parse<RegistrationStatus>(kind));
                if (kind == "complete") await TrainingTestData.CreateWaitlistEntryAsync(services, row, closed: true);
            }
        });
    }

    /// <summary>Alle moment-/zaal-/foundationkolommen worden onafhankelijk gehasht, zonder hashwaarden of persoonsgegevens uit te printen.</summary>
    private static async Task<string> FoundationDigestAsync(AccountWebApplicationFactory factory)
    {
        var digest = "";
        await factory.WithServicesAsync(async services =>
        {
            var connection = services.GetRequiredService<ApplicationDbContext>().Database.GetDbConnection();
            var opened = connection.State != ConnectionState.Open;
            if (opened) await connection.OpenAsync();
            try
            {
                var snapshot = new SortedDictionary<string, List<string?[]>>(StringComparer.Ordinal);
                foreach (var table in new[] { "Locations", "TrainingMoments", "Registrations", "WaitlistEntries" })
                {
                    await using var command = connection.CreateCommand(); command.CommandText = $"SELECT * FROM {table} ORDER BY 1,2";
                    await using var reader = await command.ExecuteReaderAsync(); var rows = new List<string?[]>();
                    while (await reader.ReadAsync())
                    {
                        var values = new string?[reader.FieldCount];
                        for (var index = 0; index < values.Length; index++) values[index] = reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
                        rows.Add(values);
                    }
                    snapshot.Add(table, rows);
                }
                digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot))));
            }
            finally { if (opened) await connection.CloseAsync(); }
        });
        return digest;
    }

    private static int[] DetailIds(string html) => Regex.Matches(html, "(?is)href\\s*=\\s*[\"']/TrainingManagement/Details/([0-9]+)(?:\\?[^\"']*)?[\"']")
        .Cast<Match>().Select(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().ToArray();
    private static bool HasControl(string html, string name) => Regex.Matches(html, "(?is)<(?:input|select|textarea)\\b[^>]*>").Cast<Match>()
        .Any(x => WebUtility.HtmlDecode(Regex.Match(x.Value, "(?is)\\bname\\s*=\\s*([\"'])(.*?)\\1").Groups[2].Value) == name);

    private static async Task LoginAsync(IdentityHttpClient browser, TestIdentity identity)
    {
        using var response = await browser.LoginAsync(identity); Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
    private static Task<HttpResponseMessage> PostAsync(IdentityHttpClient browser, string path, Dictionary<string, string> form, string token) => PostPairsAsync(browser, path, form.ToList(), token);
    private static Task<HttpResponseMessage> PostPairsAsync(IdentityHttpClient browser, string path, List<KeyValuePair<string, string>> pairs, string token)
    {
        var posted = new List<KeyValuePair<string, string>>(pairs) { new("__RequestVerificationToken", token) };
        return browser.RawClient.PostAsync(path, new FormUrlEncodedContent(posted));
    }
    private static void AssertSessionDenial(HttpResponseMessage response, string variant)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString ?? "";
        if (variant is "Pending" or "Refused") Assert.Equal("/Account/Status", location);
        else Assert.Contains("/Identity/Account/Login", location);
    }
    private static async Task<string> DigestAsync(AccountWebApplicationFactory factory, bool includeFeatures = true)
    {
        string digest = ""; await factory.WithServicesAsync(async services => digest = await TrainingTestData.DigestAsync(services, includeFeatures)); return digest;
    }
    private static async Task<TrainingEntity> ReadAsync(AccountWebApplicationFactory factory, int id)
    {
        TrainingEntity? row = null; await factory.WithServicesAsync(async services => row = await services.GetRequiredService<ApplicationDbContext>().Trainings.AsNoTracking()
            .Include(x => x.Instructor).Include(x => x.TrainingDepartments).SingleAsync(x => x.Id == id)); return row!;
    }
    private static async Task<TrainingEntity> SingleTrainingAsync(AccountWebApplicationFactory factory)
    {
        int id = 0; await factory.WithServicesAsync(async services => id = await services.GetRequiredService<ApplicationDbContext>().Trainings.Select(x => x.Id).SingleAsync()); return await ReadAsync(factory, id);
    }
    private static async Task<int[]> DepartmentIdsAsync(AccountWebApplicationFactory factory)
    {
        int[] ids = []; await factory.WithServicesAsync(async services => ids = await services.GetRequiredService<ApplicationDbContext>().Departments.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync()); return ids;
    }
    private static async Task ChangeRoleAsync(AccountWebApplicationFactory factory, string userId, string role, bool add)
    {
        await factory.WithServicesAsync(async services => { var manager = services.GetRequiredService<UserManager<ApplicationUser>>(); var user = (await manager.FindByIdAsync(userId))!;
            Assert.True((add ? await manager.AddToRoleAsync(user, role) : await manager.RemoveFromRoleAsync(user, role)).Succeeded); });
    }
    private static async Task<Dictionary<string, string>> CreateFormAsync(AccountWebApplicationFactory factory, TestIdentity owner, bool administrator, bool external)
    {
        int categoryId = 0; await factory.WithServicesAsync(async services => categoryId = (await TrainingTestData.CreateCategoryAsync(services)).Id);
        var fields = FieldsForm("Fictieve HTTP opleiding", "Fictieve HTTP toelichting", categoryId, external ? 12.30m : null, false);
        if (administrator) fields["OwnerUserId"] = owner.UserId;
        return fields;
    }
    private static Dictionary<string, string> EditForm(TrainingEntity row)
    {
        var form = FieldsForm(row.Title, row.Description, row.CategoryId, row.ExternalTotalPriceEuros, row.RequiresMotivation);
        form["ExpectedVersion"] = row.Version.ToString(); return form;
    }
    private static TrainingFields Fields(TrainingEntity row) => new(row.Title, row.Description, row.CategoryId, row.ExternalTotalPriceEuros, row.RequiresMotivation);
    private static Dictionary<string, string> FieldsForm(string title, string? description, int categoryId, decimal? price, bool motivation)
    {
        var form = new Dictionary<string, string> { ["Title"] = title, ["Description"] = description ?? "", ["CategoryId"] = categoryId.ToString(CultureInfo.InvariantCulture),
            ["RequiresMotivation"] = motivation ? "true" : "false" };
        if (price is not null) form["ExternalTotalPriceEuros"] = price.Value.ToString(CultureInfo.InvariantCulture);
        return form;
    }
    private static Dictionary<string, string> VersionForm(TrainingEntity row) => new() { ["ExpectedVersion"] = row.Version.ToString() };
    private static Dictionary<string, string> AudienceForm(TrainingEntity row, TrainingAudienceScope scope) => new() { ["ExpectedVersion"] = row.Version.ToString(), ["AudienceScope"] = scope.ToString() };
    private static List<KeyValuePair<string, string>> AudiencePairs(TrainingEntity row, TrainingAudienceScope scope, int[] ids)
    {
        var pairs = AudienceForm(row, scope).ToList(); pairs.AddRange(ids.Select(id => new KeyValuePair<string, string>("DepartmentIds", id.ToString(CultureInfo.InvariantCulture)))); return pairs;
    }
    private static void AssertFormValue(string html, string name, string expected)
    {
        foreach (Match control in Regex.Matches(html, "(?is)<input\\b[^>]*>|<textarea\\b[^>]*>.*?</textarea>"))
        {
            var attribute = Regex.Match(control.Value, "(?is)\\bname\\s*=\\s*([\"'])(.*?)\\1");
            if (WebUtility.HtmlDecode(attribute.Groups[2].Value) != name) continue;
            var value = control.Value.StartsWith("<textarea", StringComparison.OrdinalIgnoreCase)
                ? Regex.Match(control.Value, "(?is)>(.*?)</textarea>").Groups[1].Value.TrimStart('\r', '\n')
                : Regex.Match(control.Value, "(?is)\\bvalue\\s*=\\s*([\"'])(.*?)\\1").Groups[2].Value;
            Assert.Equal(expected, WebUtility.HtmlDecode(value)); return;
        }
        Assert.Fail("Het verwachte formuliercontrol ontbreekt: " + name);
    }
    private static void AssertReloadAnchor(string html, string path) => Assert.Matches("(?is)<a\\b[^>]*href\\s*=\\s*[\"']" + Regex.Escape(path) + "(?:\\?[^\"']*)?[\"']", html);
    private static void AssertNoCreatePostForm(string html)
    {
        Assert.DoesNotContain(Regex.Matches(html, "(?is)<form\\b[^>]*>").Cast<Match>(), form =>
            Regex.IsMatch(form.Value, "(?is)\\bmethod\\s*=\\s*[\"']post[\"']") && Regex.IsMatch(form.Value, "(?is)\\baction\\s*=\\s*[\"']/TrainingManagement/Create[\"']"));
    }

    /// <summary>Een zelfstandig HTTP-testscenario met tijdelijk wachtwoord, eigen cookie en eigen database.</summary>
    private sealed class Session(AccountWebApplicationFactory factory, IdentityHttpClient browser, TestIdentity owner, TestIdentity admin, bool external, bool administrator) : IAsyncDisposable
    {
        public AccountWebApplicationFactory Factory { get; } = factory;
        public IdentityHttpClient Browser { get; } = browser;
        public TestIdentity Owner { get; } = owner;
        public TestIdentity Admin { get; } = admin;
        public TestIdentity Actor => administrator ? Admin : Owner;
        public static async Task<Session> CreateAsync(bool external = false, bool administrator = false, TrainingFailureInterceptor? fault = null)
        {
            var factory = new AccountWebApplicationFactory();
            if (fault is not null) factory.ConfigureServicesBeforeStart = services =>
            {
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(factory.ConnectionString).AddInterceptors(fault));
            };
            var owner = await factory.AddAccountAsync(external ? RequestedAccountType.ExternalInstructor : RequestedAccountType.InternalInstructor, AccountApprovalStatus.Approved, [RoleNames.Lesgever]);
            var admin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
            var browser = factory.CreateIdentityClient(); await LoginAsync(browser, administrator ? admin : owner);
            return new(factory, browser, owner, admin, external, administrator);
        }
        public Task<Dictionary<string, string>> CreateFormAsync() => TrainingManagementHttpTests.CreateFormAsync(Factory, Owner, administrator, external);
        public async Task<TrainingEntity> StoreAsync(TrainingAudienceScope scope = TrainingAudienceScope.Unassigned, int[]? ids = null)
        {
            TrainingEntity? row = null; await Factory.WithServicesAsync(async services =>
            {
                var db = services.GetRequiredService<ApplicationDbContext>();
                row = await TrainingTestData.CreateTrainingAsync(services, await db.Users.SingleAsync(x => x.Id == Owner.UserId),
                    await db.Users.SingleAsync(x => x.Id == Admin.UserId), scope, ids);
            }); return row!;
        }
        public async ValueTask DisposeAsync() { Browser.Dispose(); await Factory.DisposeAsync(); }
    }
}
