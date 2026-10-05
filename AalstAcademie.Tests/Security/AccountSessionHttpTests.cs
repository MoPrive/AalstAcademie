// Sprint 003: Behoudt de echte cookie- en toegangsregressies terwijl de demostart verandert
// Een oude sessie mag na een volledige demoheropbouw geen nieuwe accountrechten overnemen.
using System.Net;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AalstAcademie.Tests.Security;

/// <summary>Echte wachtwoordcookies bewijzen actuele accountrechten, sessie-intrekking en serverroutegrenzen.</summary>
public class AccountSessionHttpTests
{
    // Navigatie krijgt een loginredirect; de snapshotclient krijgt een HTTP-status die hij direct kan afhandelen.
    [Fact]
    public async Task Anonymous_private_navigation_challenges_login_and_snapshot_returns_401_without_html()
    {
        await using var factory = new AccountWebApplicationFactory();
        using var browser = factory.CreateIdentityClient();
        foreach (var path in new[] { "/", "/Account/Status", "/Admin/AccountApplications" })
        {
            using var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Identity/Account/Login", response.Headers.Location?.OriginalString);
        }
        using var snapshot = await browser.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        Assert.Equal(HttpStatusCode.Unauthorized, snapshot.StatusCode);
        Assert.DoesNotContain("<html", await snapshot.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // Pending mag de eigen status lezen en uitloggen. Een userId-query mag nooit een andere identiteit selecteren.
    [Fact]
    public async Task Pending_login_can_read_own_status_but_home_admin_and_snapshot_remain_unavailable()
    {
        await using var factory = new AccountWebApplicationFactory();
        var identity = await factory.AddAccountAsync(RequestedAccountType.Employee);
        using var browser = factory.CreateIdentityClient();
        using var login = await browser.LoginAsync(identity);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/Account/Status", login.Headers.Location?.OriginalString);
        using var status = await browser.GetAsync("/Account/Status?userId=another-user");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        foreach (var path in new[] { "/", "/Admin/AccountApplications" })
        {
            using var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/Account/Status", response.Headers.Location?.OriginalString);
        }
        using var snapshot = await browser.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, snapshot.StatusCode);
        await AssertLogoutEndsSessionAsync(browser);
    }

    // Razor moet de opgeslagen reden encoderen; een geweigerd account krijgt daardoor geen script- of apprechten.
    [Fact]
    public async Task Refused_login_displays_only_own_escaped_reason_and_has_no_application_access()
    {
        await using var factory = new AccountWebApplicationFactory();
        var identity = await factory.AddAccountAsync(RequestedAccountType.Employee, AccountApprovalStatus.Refused);
        await factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == identity.UserId);
            user.RefusalReason = "Eigen reden <script>niet-uitvoeren</script>";
            await db.SaveChangesAsync();
        });
        using var browser = factory.CreateIdentityClient();
        using var login = await browser.LoginAsync(identity);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var status = await browser.GetAsync("/Account/Status");
        var html = await status.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Contains("Eigen reden", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>niet-uitvoeren", html);
        using var home = await browser.GetAsync("/");
        Assert.Equal("/Account/Status", home.Headers.Location?.OriginalString);
        await AssertLogoutEndsSessionAsync(browser);
    }

    /// <summary>Een redirect alleen bewijst geen logout: het volgende verzoek moet echt anoniem zijn.</summary>
    private static async Task AssertLogoutEndsSessionAsync(IdentityHttpClient browser)
    {
        using var logout = await browser.PostFormAsync("/Identity/Account/Logout?returnUrl=/Identity/Account/Login",
            new Dictionary<string, string>(), "/Account/Status");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Contains("/Identity/Account/Login", logout.Headers.Location?.OriginalString);
        using var statusAfterLogout = await browser.GetAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.Redirect, statusAfterLogout.StatusCode);
        Assert.Contains("/Identity/Account/Login", statusAfterLogout.Headers.Location?.OriginalString);
        using var anonymousSnapshot = await browser.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousSnapshot.StatusCode);
    }

    // De drie goedgekeurde deelnemertypen delen toegang tot Home, maar krijgen geen beheerrechten.
    [Theory]
    [InlineData(RequestedAccountType.Employee)]
    [InlineData(RequestedAccountType.InternalInstructor)]
    [InlineData(RequestedAccountType.ExternalInstructor)]
    public async Task Approved_participant_has_home_access_but_cannot_open_administration(RequestedAccountType type)
    {
        await using var factory = new AccountWebApplicationFactory();
        var identity = await factory.AddAccountAsync(type, AccountApprovalStatus.Approved, RoleNames.ForApprovedAccountType(type));
        using var browser = factory.CreateIdentityClient();
        using var login = await browser.LoginAsync(identity);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var home = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        using var administration = await browser.GetAsync("/Admin/AccountApplications");
        AssertDenied(administration);
        using var snapshot = await browser.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, snapshot.StatusCode);
    }

    // Het positieve controlepad voorkomt dat alle negatieve beheertests alleen door een kapotte route slagen.
    [Fact]
    public async Task Approved_unblocked_administrator_can_open_queue_details_and_snapshot()
    {
        await using var factory = new AccountWebApplicationFactory();
        var administrator = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var applicant = await factory.AddAccountAsync(RequestedAccountType.Employee);
        using var browser = factory.CreateIdentityClient();
        using var login = await browser.LoginAsync(administrator);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        foreach (var path in new[] { "/", "/Admin/AccountApplications", $"/Admin/AccountApplications/{applicant.UserId}" })
        {
            using var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var snapshot = await browser.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
    }

    // Tokenhoudende vervalste POSTs bewijzen dat identiteit alleen geen beheertoestemming is.
    [Fact]
    public async Task Pending_and_approved_nonadministrator_cannot_execute_forged_review_posts()
    {
        await using var factory = new AccountWebApplicationFactory();
        var target = await factory.AddAccountAsync(RequestedAccountType.Employee);
        foreach (var approval in new[] { AccountApprovalStatus.Pending, AccountApprovalStatus.Approved })
        {
            var actor = await factory.AddAccountAsync(RequestedAccountType.Employee, approval,
                approval == AccountApprovalStatus.Approved ? [RoleNames.Medewerker] : []);
            using var browser = factory.CreateIdentityClient();
            using var login = await browser.LoginAsync(actor);
            var fields = await ReviewFieldsAsync(factory, target);
            using var result = await browser.PostFormAsync($"/Admin/AccountApplications/{target.UserId}/Approve", fields, tokenPage: "/Account/Status");
            AssertDenied(result, allowStatusRedirect: true);
        }
        await AssertStateAsync(factory, target.UserId, AccountApprovalStatus.Pending);
    }

    // Een geldige beheerdercookie vervangt geen CSRF-token; een ongeautoriseerde cross-site write moet stoppen.
    [Fact]
    public async Task Administrator_review_without_antiforgery_is_rejected_before_state_change()
    {
        await using var factory = new AccountWebApplicationFactory();
        var admin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var target = await factory.AddAccountAsync(RequestedAccountType.Employee);
        using var browser = factory.CreateIdentityClient();
        using var login = await browser.LoginAsync(admin);
        using var result = await browser.RawClient.PostAsync($"/Admin/AccountApplications/{target.UserId}/Approve",
            new FormUrlEncodedContent(await ReviewFieldsAsync(factory, target)));
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        await AssertStateAsync(factory, target.UserId, AccountApprovalStatus.Pending);
    }

    // Alle reviewroutes delen modelbinding. Een onleesbaar getal mag niet als een
    // ontbrekende optionele afdeling doorgaan naar de service en toch iets opslaan.
    [Fact]
    public async Task Malformed_review_binding_preserves_state_and_original_input_before_valid_retry()
    {
        await using var factory = new AccountWebApplicationFactory();
        var admin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var approvalTarget = await factory.AddAccountAsync(RequestedAccountType.InternalInstructor);
        var refusalTarget = await factory.AddAccountAsync(RequestedAccountType.InternalInstructor,
            roles: [RoleNames.Lesgever]);
        using var browser = factory.CreateIdentityClient();
        using var login = await browser.LoginAsync(admin);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        await RejectInvalidThenRetryAsync(approvalTarget, "Approve", AccountApprovalStatus.Approved);
        await RejectInvalidThenRetryAsync(refusalTarget, "Refuse", AccountApprovalStatus.Refused);
        await RejectInvalidThenRetryAsync(refusalTarget, "Reopen", AccountApprovalStatus.Pending);

        async Task RejectInvalidThenRetryAsync(TestIdentity target, string action, AccountApprovalStatus expectedStatus)
        {
            var before = await ReadReviewStateAsync(factory, target.UserId);
            var originalFields = await ReviewFieldsAsync(factory, target);
            const string reason = "Deze fictieve reden blijft bij een bindingsfout behouden.";
            if (action == "Refuse") originalFields["RefusalReason"] = reason;
            var invalidFields = new Dictionary<string, string>(originalFields) { ["DepartmentId"] = "abc" };
            var route = $"/Admin/AccountApplications/{target.UserId}";

            // Echte beheerdercookie, geldige CSRF en geldige oorspronkelijke versie:
            // uitsluitend de foutieve bindingswaarde verhindert deze beslissing.
            using var invalid = await browser.PostFormAsync($"{route}/{action}", invalidFields, tokenPage: route);
            Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
            var html = await invalid.Content.ReadAsStringAsync();
            Assert.Contains("validation-summary-errors", html);
            Assert.Contains("abc", html);
            Assert.Contains($"value=\"{before.ConcurrencyStamp}\"", html);
            if (action == "Refuse") Assert.Contains(reason, html);
            var unchanged = await ReadReviewStateAsync(factory, target.UserId);
            Assert.Equal(before.Status, unchanged.Status);
            Assert.Equal(before.DepartmentId, unchanged.DepartmentId);
            Assert.Equal(before.SecurityStamp, unchanged.SecurityStamp);
            Assert.Equal(before.ConcurrencyStamp, unchanged.ConcurrencyStamp);
            Assert.Equal(before.ReviewedById, unchanged.ReviewedById);
            Assert.Equal(before.ReviewedAt, unchanged.ReviewedAt);
            Assert.Equal(before.RefusalReason, unchanged.RefusalReason);
            Assert.Equal(before.Roles, unchanged.Roles);

            // Geen nieuwe versie opvragen: dezelfde bewust gelezen versie moet na
            // herstel van de invoer nog geldig zijn, met de normale PRG-uitkomst.
            using var retry = await browser.PostFormAsync($"{route}/{action}", originalFields, tokenPage: route);
            Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
            await AssertStateAsync(factory, target.UserId, expectedStatus);
            var accepted = await ReadReviewStateAsync(factory, target.UserId);
            Assert.Equal(before.DepartmentId, accepted.DepartmentId);
            Assert.NotEqual(before.SecurityStamp, accepted.SecurityStamp);
            Assert.NotEqual(before.ConcurrencyStamp, accepted.ConcurrencyStamp);
            Assert.Equal(admin.UserId, accepted.ReviewedById);
            Assert.NotNull(accepted.ReviewedAt);
            Assert.Equal(action == "Refuse" ? reason : null, accepted.RefusalReason);
        }
    }

    // Een blokkering is onmiddellijk actuele database-informatie en verhindert zowel cookiegebruik als nieuwe login.
    [Fact]
    public async Task Blocking_current_account_rejects_existing_cookie_and_new_password_login()
    {
        await using var factory = new AccountWebApplicationFactory();
        var identity = await factory.AddAccountAsync(RequestedAccountType.Employee, AccountApprovalStatus.Approved, [RoleNames.Medewerker]);
        using var browser = factory.CreateIdentityClient();
        using var login = await browser.LoginAsync(identity);
        await factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == identity.UserId);
            user.IsBlocked = true;
            await db.SaveChangesAsync();
        });
        using var oldCookie = await browser.GetAjaxAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.Unauthorized, oldCookie.StatusCode);
        using var freshBrowser = factory.CreateIdentityClient();
        using var newLogin = await freshBrowser.LoginAsync(identity);
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        using var denied = await freshBrowser.GetAjaxAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    // Een nog aanwezige ondertekende cookie is geen bewijs dat het account nog bestaat.
    [Fact]
    public async Task Deleted_account_cookie_is_rejected_on_next_request()
    {
        await using var factory = new AccountWebApplicationFactory();
        var identity = await factory.AddAccountAsync(RequestedAccountType.Employee);
        using var browser = factory.CreateIdentityClient();
        using var login = await browser.LoginAsync(identity);
        await factory.WithServicesAsync(async services =>
        {
            var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await manager.DeleteAsync((await manager.FindByIdAsync(identity.UserId))!)).Succeeded);
        });
        using var response = await browser.GetAjaxAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // SecurityStamp is sessieversie: een wijziging vereist opnieuw aanmelden en mag niet stil worden vernieuwd.
    [Fact]
    public async Task Security_stamp_mismatch_rejects_cookie_without_silent_renewal_and_new_login_works()
    {
        await using var factory = new AccountWebApplicationFactory();
        var identity = await factory.AddAccountAsync(RequestedAccountType.Employee, AccountApprovalStatus.Approved, [RoleNames.Medewerker]);
        using var browser = factory.CreateIdentityClient();
        using var login = await browser.LoginAsync(identity);
        await factory.WithServicesAsync(async services =>
        {
            var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await manager.UpdateSecurityStampAsync((await manager.FindByIdAsync(identity.UserId))!)).Succeeded);
        });
        using var firstDenied = await browser.GetAjaxAsync("/Account/Status");
        using var stillDenied = await browser.GetAjaxAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.Unauthorized, firstDenied.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stillDenied.StatusCode);
        using var newLogin = await browser.LoginAsync(identity);
        Assert.Equal(HttpStatusCode.Redirect, newLogin.StatusCode);
        using var accepted = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    // Rechtstreeks rollen verwijderen laat expres oude cookieclaims staan; actuele autorisatie moet toch weigeren.
    [Fact]
    public async Task Revoked_administrator_role_is_enforced_from_database_even_when_cookie_claim_is_old()
    {
        await using var factory = new AccountWebApplicationFactory();
        var identity = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        using var browser = factory.CreateIdentityClient();
        using var login = await browser.LoginAsync(identity);
        await factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            await db.UserRoles.Where(x => x.UserId == identity.UserId).ExecuteDeleteAsync();
        });
        using var snapshot = await browser.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, snapshot.StatusCode);
        Assert.DoesNotContain("<html", await snapshot.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // Een echte beheerder-POST trekt de aanvragerscookie in; dezelfde wachtwoordlogin krijgt pas daarna rechten.
    [Fact]
    public async Task Real_approval_rotates_applicant_session_and_new_login_enters_approved_area()
    {
        await using var factory = new AccountWebApplicationFactory();
        var admin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var applicant = await factory.AddAccountAsync(RequestedAccountType.InternalInstructor);
        using var applicantBrowser = factory.CreateIdentityClient();
        using var initialLogin = await applicantBrowser.LoginAsync(applicant);
        using var administratorBrowser = factory.CreateIdentityClient();
        using var adminLogin = await administratorBrowser.LoginAsync(admin);
        using var review = await administratorBrowser.PostFormAsync($"/Admin/AccountApplications/{applicant.UserId}/Approve",
            await ReviewFieldsAsync(factory, applicant), tokenPage: $"/Admin/AccountApplications/{applicant.UserId}");
        Assert.Equal(HttpStatusCode.Redirect, review.StatusCode);
        await AssertStateAsync(factory, applicant.UserId, AccountApprovalStatus.Approved);
        using var oldSession = await applicantBrowser.GetAjaxAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        using var newLogin = await applicantBrowser.LoginAsync(applicant);
        Assert.Equal(HttpStatusCode.Redirect, newLogin.StatusCode);
        using var home = await applicantBrowser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    // Beide echte beheerbeslissingen draaien de sessieversie; opnieuw aanmelden blijft uitsluitend statuslezing geven.
    [Fact]
    public async Task Real_refusal_and_reopening_each_require_new_applicant_login_and_preserve_status_only_access()
    {
        await using var factory = new AccountWebApplicationFactory();
        var admin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var applicant = await factory.AddAccountAsync(RequestedAccountType.Employee);
        using var applicantBrowser = factory.CreateIdentityClient();
        using var initialLogin = await applicantBrowser.LoginAsync(applicant);
        using var administratorBrowser = factory.CreateIdentityClient();
        using var adminLogin = await administratorBrowser.LoginAsync(admin);
        var refusal = await ReviewFieldsAsync(factory, applicant);
        refusal["RefusalReason"] = "Fictieve controle vraagt aanvulling.";
        using var refused = await administratorBrowser.PostFormAsync($"/Admin/AccountApplications/{applicant.UserId}/Refuse", refusal,
            tokenPage: $"/Admin/AccountApplications/{applicant.UserId}");
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        using var revoked = await applicantBrowser.GetAjaxAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var refusedLogin = await applicantBrowser.LoginAsync(applicant);
        using var refusedStatus = await applicantBrowser.GetAsync("/Account/Status");
        Assert.Contains("Fictieve controle vraagt aanvulling.", await refusedStatus.Content.ReadAsStringAsync());
        using var reopened = await administratorBrowser.PostFormAsync($"/Admin/AccountApplications/{applicant.UserId}/Reopen",
            await ReviewFieldsAsync(factory, applicant), tokenPage: $"/Admin/AccountApplications/{applicant.UserId}");
        Assert.Equal(HttpStatusCode.Redirect, reopened.StatusCode);
        using var revokedAgain = await applicantBrowser.GetAjaxAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.Unauthorized, revokedAgain.StatusCode);
        using var pendingLogin = await applicantBrowser.LoginAsync(applicant);
        Assert.Equal("/Account/Status", pendingLogin.Headers.Location?.OriginalString);
        await AssertStateAsync(factory, applicant.UserId, AccountApprovalStatus.Pending);
    }

    // De demo-uitzondering mag het bestaande bevestigingsbeleid voor gewone Production-accounts niet versoepelen.
    [Fact]
    public async Task Non_demo_retains_confirmation_requirement_for_existing_local_accounts()
    {
        await using var factory = new AccountWebApplicationFactory(Environments.Production, demoEnabled: false);
        var unconfirmed = await factory.AddAccountAsync(RequestedAccountType.Employee, AccountApprovalStatus.Approved, [RoleNames.Medewerker]);
        var confirmed = await factory.AddAccountAsync(RequestedAccountType.Employee, AccountApprovalStatus.Approved,
            [RoleNames.Medewerker], emailConfirmed: true);
        using var browser = factory.CreateIdentityClient();
        using var deniedLogin = await browser.LoginAsync(unconfirmed);
        Assert.Equal(HttpStatusCode.OK, deniedLogin.StatusCode);
        using var denied = await browser.GetAjaxAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var acceptedLogin = await browser.LoginAsync(confirmed);
        Assert.Equal(HttpStatusCode.Redirect, acceptedLogin.StatusCode);
        using var home = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    // DemoSeedKey sluit testidentiteiten buiten demo uit, zelfs wanneer hun e-mailveld als bevestigd staat.
    [Fact]
    public async Task Demo_marked_account_cannot_login_outside_demo_even_when_email_is_confirmed()
    {
        await using var factory = new AccountWebApplicationFactory(Environments.Production, demoEnabled: false);
        var identity = await factory.AddAccountAsync(RequestedAccountType.Employee, AccountApprovalStatus.Approved,
            [RoleNames.Medewerker], emailConfirmed: true, demoSeedKey: "fictief.test.demo");
        using var browser = factory.CreateIdentityClient();
        using var login = await browser.LoginAsync(identity);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var status = await browser.GetAjaxAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    // Ongeldige demo-configuratie moet stoppen voordat zelfs een leeg databasebestand wordt aangemaakt.
    [Fact]
    public async Task Enabling_demo_outside_development_fails_host_start_before_database_initialization()
    {
        await using var factory = new AccountWebApplicationFactory(Environments.Production, demoEnabled: true);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateIdentityClient());
        Assert.Contains("Demo:Enabled", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(factory.DatabasePath));
        Assert.False(File.Exists(factory.DatabasePath + ".application.lock"));

        // Ook een reeds bestaande eigen SQLite-file blijft volledig intact: geen schema-, reset- of seedwrite.
        await using var existing = new AccountWebApplicationFactory(Environments.Production, demoEnabled: true);
        await using (var connection = new SqliteConnection(existing.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE FictieveSentinel (Id INTEGER PRIMARY KEY, Value TEXT); INSERT INTO FictieveSentinel VALUES (1,'behouden')";
            await command.ExecuteNonQueryAsync();
        }
        var before = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(existing.DatabasePath));
        var existingError = Assert.ThrowsAny<Exception>(() => existing.CreateIdentityClient());
        Assert.Contains("Demo:Enabled", existingError.ToString(), StringComparison.Ordinal);
        var after = System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(existing.DatabasePath));
        Assert.True(before.SequenceEqual(after), "Verboden productiedemo mag geen bestaand testbestand wijzigen.");
        Assert.False(File.Exists(existing.DatabasePath + ".application.lock"));
        await using (var connection = new SqliteConnection(existing.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name <> 'FictieveSentinel'";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
            command.CommandText = "SELECT Value FROM FictieveSentinel WHERE Id=1";
            Assert.Equal("behouden", await command.ExecuteScalarAsync());
        }
    }

    // Verborgen links volstaan niet: rechtstreeks GET én POST blijven ook met een geldige cookie onbeschikbaar.
    [Fact]
    public async Task Unsupported_bundled_identity_routes_never_execute_for_anonymous_or_pending_accounts()
    {
        await using var factory = new AccountWebApplicationFactory();
        var identity = await factory.AddAccountAsync(RequestedAccountType.Employee);
        using var anonymous = factory.CreateIdentityClient();
        using var pending = factory.CreateIdentityClient();
        using var login = await pending.LoginAsync(identity);
        foreach (var browser in new[] { anonymous, pending })
        foreach (var path in new[]
        {
            "/Identity/Account/ForgotPassword", "/Identity/Account/ForgotPasswordConfirmation", "/Identity/Account/ResetPassword",
            "/Identity/Account/ResetPasswordConfirmation", "/Identity/Account/ConfirmEmail?userId=fictief&code=fictief",
            "/Identity/Account/ExternalLogin", "/Identity/Account/Manage", "/Identity/Account/Manage/ChangePassword",
            "/Identity/Account/Manage/Email", "/Identity/Account/Manage/EnableAuthenticator"
        })
        {
            using var get = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
            using var post = await browser.RawClient.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        }
    }

    // De gewone Identity-lockout blijft actief: businessstatuscontrole vervangt geen wachtwoordbeveiliging.
    [Fact]
    public async Task Wrong_password_never_authenticates_and_repeated_failures_use_identity_lockout()
    {
        await using var factory = new AccountWebApplicationFactory();
        var identity = await factory.AddAccountAsync(RequestedAccountType.Employee, AccountApprovalStatus.Approved, [RoleNames.Medewerker]);
        using var browser = factory.CreateIdentityClient();
        var wrong = identity with { Password = AccountWebApplicationFactory.NewPassword() };
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var login = await browser.LoginAsync(wrong);
            Assert.True(login.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect);
        }
        using var denied = await browser.GetAjaxAsync("/Account/Status");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await factory.WithServicesAsync(async services =>
        {
            var user = await services.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(x => x.Id == identity.UserId);
            Assert.NotNull(user.LockoutEnd);
        });
    }

    /// <summary>Accepteert de bedoelde navigatie-uitkomst; een redirect naar een willekeurige succesroute geldt niet.</summary>
    private static void AssertDenied(HttpResponseMessage response, bool allowStatusRedirect = false)
    {
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
        if (response.StatusCode != HttpStatusCode.Redirect) return;
        var path = response.Headers.Location?.OriginalString ?? "";
        Assert.True(path.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ||
            (allowStatusRedirect && path == "/Account/Status"));
    }

    /// <summary>Leest de versie één keer vóór POST, net zoals een geopend beheerformulier dat doet.</summary>
    private static async Task<Dictionary<string, string>> ReviewFieldsAsync(AccountWebApplicationFactory factory, TestIdentity identity)
    {
        var fields = new Dictionary<string, string>();
        await factory.WithServicesAsync(async services =>
        {
            var user = await services.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(x => x.Id == identity.UserId);
            fields["ExpectedConcurrencyStamp"] = user.ConcurrencyStamp!;
            if (user.DepartmentId is not null) fields["DepartmentId"] = user.DepartmentId.Value.ToString();
        });
        return fields;
    }

    /// <summary>Een nieuwe HTTP-testscope leest de opgeslagen reviewvelden en roltoekenningen opnieuw.</summary>
    private static async Task<ReviewState> ReadReviewStateAsync(AccountWebApplicationFactory factory, string id)
    {
        ReviewState? state = null;
        await factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id);
            var roles = await services.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(user);
            state = new(user.AccountApprovalStatus, user.DepartmentId, user.SecurityStamp, user.ConcurrencyStamp,
                user.ReviewedById, user.ReviewedAt, user.RefusalReason, roles.Order().ToArray());
        });
        return state!;
    }

    /// <summary>Bundelt alle beslissingsvelden en rechten die een bindingsfout ongewijzigd moet laten.</summary>
    private sealed record ReviewState(AccountApprovalStatus Status, int? DepartmentId, string? SecurityStamp,
        string? ConcurrencyStamp, string? ReviewedById, DateTimeOffset? ReviewedAt, string? RefusalReason, string[] Roles);

    /// <summary>Een nieuwe scope controleert duurzame status én de exacte bijbehorende rollencombinatie.</summary>
    private static Task AssertStateAsync(AccountWebApplicationFactory factory, string id, AccountApprovalStatus status) =>
        factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == id);
            Assert.Equal(status, user.AccountApprovalStatus);
            Assert.False(user.EmailConfirmed);
            var roles = await services.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(user);
            Assert.Equal(status == AccountApprovalStatus.Approved ? RoleNames.ForApprovedAccountType(user.RequestedAccountType).Order().ToArray() : [],
                roles.Order().ToArray());
        });
}
