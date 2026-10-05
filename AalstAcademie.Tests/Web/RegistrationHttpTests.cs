using System.Net;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AalstAcademie.Tests.Web;

/// <summary>Registratie via echte Razor Pages, antiforgery en Identity-cookie tegen een unieke SQLite-file.</summary>
public class RegistrationHttpTests
{
    // Elke hoedanigheid moet zijn eigen profiel krijgen zonder rechten of e-mailbevestiging.
    [Theory]
    [InlineData(RequestedAccountType.Employee)]
    [InlineData(RequestedAccountType.InternalInstructor)]
    [InlineData(RequestedAccountType.ExternalInstructor)]
    public async Task Valid_registration_creates_pending_matching_profile_and_signs_in_only_to_own_status(RequestedAccountType type)
    {
        await using var factory = new AccountWebApplicationFactory();
        using var browser = factory.CreateIdentityClient();
        var form = await RegistrationFormAsync(factory, type);
        using var response = await browser.PostFormAsync("/Identity/Account/Register", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Status", response.Headers.Location?.OriginalString);
        using var status = await browser.GetAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        await factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync();
            Assert.Equal(AccountApprovalStatus.Pending, user.AccountApprovalStatus);
            Assert.Equal(type, user.RequestedAccountType);
            Assert.False(user.EmailConfirmed);
            Assert.False(user.IsBlocked);
            Assert.Null(user.DemoSeedKey);
            Assert.NotNull(user.AccountRequestedAtUtc);
            Assert.Empty(await services.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(user));
            Assert.Equal(type == RequestedAccountType.InternalInstructor,
                await db.InternalInstructors.AnyAsync(x => x.ApplicationUserId == user.Id));
            Assert.Equal(type == RequestedAccountType.ExternalInstructor,
                await db.ExternalInstructors.AnyAsync(x => x.ApplicationUserId == user.Id));
            Assert.Equal(type != RequestedAccountType.ExternalInstructor, user.DepartmentId is not null);
        });
    }

    // Beide fouten worden serverzijdig afgewezen, ook wanneer een browser irrelevante controls zelf post.
    [Fact]
    public async Task Invalid_common_fields_and_irrelevant_type_fields_show_errors_without_partial_account_or_password_echo()
    {
        await using var factory = new AccountWebApplicationFactory();
        using var browser = factory.CreateIdentityClient();
        var invalid = await RegistrationFormAsync(factory, RequestedAccountType.Employee);
        invalid["Input.PhoneNumber"] = "geen telefoon";
        using var response = await browser.PostFormAsync("/Identity/Account/Register", invalid);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("validation", html, StringComparison.OrdinalIgnoreCase);
        Assert.False(html.Contains(invalid["Input.Password"], StringComparison.Ordinal));
        var irrelevant = await RegistrationFormAsync(factory, RequestedAccountType.ExternalInstructor);
        irrelevant["Input.FirstName"] = "Mag niet bij deze organisatie";
        using var second = await browser.PostFormAsync("/Identity/Account/Register", irrelevant);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        await factory.WithServicesAsync(async services => Assert.Equal(0, await services.GetRequiredService<ApplicationDbContext>().Users.CountAsync()));
    }

    // Systeemvelden staan niet in het bindbare invoermodel en kunnen geen rol of goedkeuring opleveren.
    [Fact]
    public async Task Forged_privilege_and_demo_fields_cannot_escape_the_pending_registration_contract()
    {
        await using var factory = new AccountWebApplicationFactory();
        using var browser = factory.CreateIdentityClient();
        var form = await RegistrationFormAsync(factory, RequestedAccountType.Employee);
        foreach (var prefix in new[] { "", "Input." })
        {
            form[prefix + "Role"] = RoleNames.Beheerder;
            form[prefix + "AccountApprovalStatus"] = nameof(AccountApprovalStatus.Approved);
            form[prefix + "IsBlocked"] = "true";
            form[prefix + "ReviewedById"] = "forged-reviewer";
            form[prefix + "DemoSeedKey"] = "forged.demo";
        }
        using var response = await browser.PostFormAsync("/Identity/Account/Register", form);
        // Sprint 004: de whitelist weigert extra systeemvelden expliciet vóór enige account-/profielwrite.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            Assert.Empty(await db.Users.ToListAsync()); Assert.Empty(await db.UserRoles.ToListAsync());
            Assert.Empty(await db.InternalInstructors.ToListAsync()); Assert.Empty(await db.ExternalInstructors.ToListAsync());
        });
    }

    // Twee echte browsers mogen dezelfde mail ook bij gelijktijdige inzending niet dubbel opslaan.
    [Fact]
    public async Task Repeated_email_with_case_and_whitespace_returns_validation_and_keeps_one_account()
    {
        await using var factory = new AccountWebApplicationFactory();
        using var firstBrowser = factory.CreateIdentityClient();
        using var secondBrowser = factory.CreateIdentityClient();
        var form = await RegistrationFormAsync(factory, RequestedAccountType.InternalInstructor);
        var variant = new Dictionary<string, string>(form)
        {
            ["Input.Email"] = " " + form["Input.Email"].ToUpperInvariant() + " "
        };
        // Haal beide cookie/tokenparen vooraf op: tokenverkeer mag de schrijfrace niet serialiseren.
        form["__RequestVerificationToken"] = await firstBrowser.GetAntiforgeryTokenAsync("/Identity/Account/Register");
        variant["__RequestVerificationToken"] = await secondBrowser.GetAntiforgeryTokenAsync("/Identity/Account/Register");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> SendWhenReleasedAsync(IdentityHttpClient browser, Dictionary<string, string> fields)
        {
            await start.Task;
            return await browser.RawClient.PostAsync("/Identity/Account/Register", new FormUrlEncodedContent(fields));
        }
        var firstSubmission = Task.Run(() => SendWhenReleasedAsync(firstBrowser, form));
        var secondSubmission = Task.Run(() => SendWhenReleasedAsync(secondBrowser, variant));
        start.SetResult();
        var responses = await Task.WhenAll(firstSubmission, secondSubmission);
        using var first = responses[0];
        using var second = responses[1];
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
        var rejected = Assert.Single(responses, response => response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict);
        Assert.Contains("validation-summary-errors", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var winner = first.StatusCode == HttpStatusCode.Redirect ? firstBrowser : secondBrowser;
        using var ownStatus = await winner.GetAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.OK, ownStatus.StatusCode);

        // Ook een gewone volgende poging houdt het bestaande account/profiel intact.
        using var duplicate = await secondBrowser.PostFormAsync("/Identity/Account/Register", variant);
        Assert.True(duplicate.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict);
        await factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var stored = await db.Users.SingleAsync();
            Assert.Equal(form["Input.Email"].Trim().ToUpperInvariant(), stored.NormalizedEmail);
            Assert.Equal(AccountApprovalStatus.Pending, stored.AccountApprovalStatus);
            Assert.False(stored.EmailConfirmed);
            Assert.Equal(stored.Id, (await db.InternalInstructors.SingleAsync()).ApplicationUserId);
            Assert.Empty(await db.ExternalInstructors.ToListAsync());
            Assert.Empty(await db.UserRoles.ToListAsync());
        });
    }

    // Zonder verborgen token/cookiecombinatie bereikt het verzoek de registratieservice niet.
    [Fact]
    public async Task Registration_without_antiforgery_is_rejected_without_account_creation()
    {
        await using var factory = new AccountWebApplicationFactory();
        using var browser = factory.CreateIdentityClient();
        var form = await RegistrationFormAsync(factory, RequestedAccountType.Employee);
        using var response = await browser.RawClient.PostAsync("/Identity/Account/Register", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await factory.WithServicesAsync(async services => Assert.Equal(0, await services.GetRequiredService<ApplicationDbContext>().Users.CountAsync()));
    }

    // Buiten de demo is geen werkende mailflow beloofd; ook een rechtstreeks POST maakt niets aan.
    [Fact]
    public async Task Non_demo_registration_get_and_post_explain_unavailability_without_email_claim_or_mutation()
    {
        await using var factory = new AccountWebApplicationFactory(Environments.Production, demoEnabled: false);
        using var browser = factory.CreateIdentityClient();
        using var get = await browser.GetAsync("/Identity/Account/Register");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var html = await get.Content.ReadAsStringAsync();
        Assert.Contains("beheerder", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConfirmEmail", html, StringComparison.OrdinalIgnoreCase);
        var form = await RegistrationFormAsync(factory, RequestedAccountType.Employee);
        using var post = await browser.PostFormAsync("/Identity/Account/Register", form);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Contains("beheerder", await post.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await factory.WithServicesAsync(async services => Assert.Equal(0, await services.GetRequiredService<ApplicationDbContext>().Users.CountAsync()));
    }

    /// <summary>Maakt alleen bindbare velden voor de gekozen hoedanigheid; CSRF wordt door de browserhelper toegevoegd.</summary>
    private static async Task<Dictionary<string, string>> RegistrationFormAsync(AccountWebApplicationFactory factory, RequestedAccountType type)
    {
        int departmentId = 0;
        await factory.WithServicesAsync(async services => departmentId = await services.GetRequiredService<ApplicationDbContext>().Departments.Select(x => x.Id).FirstAsync());
        var password = AccountWebApplicationFactory.NewPassword();
        var form = new Dictionary<string, string>
        {
            ["Input.RequestedAccountType"] = ((int)type).ToString(),
            ["Input.Email"] = $"aanvraag-{Guid.NewGuid():N}@example.invalid",
            ["Input.PhoneNumber"] = "0490000000", ["Input.Password"] = password, ["Input.ConfirmPassword"] = password
        };
        // Interne persoonsgegevens en externe organisatiegegevens zijn aparte inputvormen, geen extra rechten.
        if (type == RequestedAccountType.ExternalInstructor) form["Input.OrganizationName"] = "Fictieve Test Academie";
        else
        {
            form["Input.FirstName"] = "Noor";
            form["Input.LastName"] = "Peeters";
            form["Input.DepartmentId"] = departmentId.ToString();
            if (type == RequestedAccountType.Employee) form["Input.ManagerUserId"] = "";
        }
        return form;
    }
}
