using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Accounts;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Web;

/// <summary>Echte beheerderscookies lezen actuele, beperkte snapshots uit een eigen SQLite-database per testcase.</summary>
public class AccountApplicationsSnapshotHttpTests
{
    // De snapshot is de actuele waarheid voor de browser; een SignalR-event draagt zelf geen accountgegevens.
    // Deze HTTP-tests toetsen het wire-contract en de database-uitkomst, los van DOM-rendering.
    // Beide beheerders moeten dezelfde commit zien; een registratiecookie verleent geen beheertoegang.
    [Fact]
    public async Task Committed_http_registration_updates_count_and_pending_rows_for_two_administrator_sessions()
    {
        await using var factory = new AccountWebApplicationFactory();
        var firstAdmin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var secondAdmin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        using var first = factory.CreateIdentityClient();
        using var second = factory.CreateIdentityClient();
        using var applicant = factory.CreateIdentityClient();
        await LoginAdministratorAsync(first, firstAdmin);
        await LoginAdministratorAsync(second, secondAdmin);
        await AssertBothSnapshotsAsync(first, second, AccountApprovalStatus.Pending, 0, []);

        var departmentId = 0;
        await factory.WithServicesAsync(async services => departmentId = await services
            .GetRequiredService<ApplicationDbContext>().Departments.Select(x => x.Id).FirstAsync());
        var password = AccountWebApplicationFactory.NewPassword();
        var email = $"snapshot-aanvraag-{Guid.NewGuid():N}@example.invalid";
        using var registered = await applicant.PostFormAsync("/Identity/Account/Register", new Dictionary<string, string>
        {
            ["Input.RequestedAccountType"] = ((int)RequestedAccountType.Employee).ToString(),
            ["Input.Email"] = email, ["Input.PhoneNumber"] = "0490000000",
            ["Input.Password"] = password, ["Input.ConfirmPassword"] = password,
            ["Input.FirstName"] = "Noor", ["Input.LastName"] = "Peeters",
            ["Input.ManagerUserId"] = "", ["Input.DepartmentId"] = departmentId.ToString()
        });
        Assert.Equal(HttpStatusCode.Redirect, registered.StatusCode);
        string? userId = null;
        await factory.WithServicesAsync(async services => userId = await services.GetRequiredService<ApplicationDbContext>()
            .Users.Where(x => x.Email == email).Select(x => x.Id).SingleAsync());

        // Onafhankelijke HTTP-verzoeken lezen ieder hun eigen scoped verbinding en dezelfde opgeslagen aanvraag.
        await AssertBothSnapshotsAsync(first, second, AccountApprovalStatus.Pending, 1, [userId!]);
        var snapshot = await ReadSnapshotAsync(first);
        Assert.Equal("Noor Peeters", Assert.Single(snapshot.Rows).DisplayName);
        Assert.Equal(nameof(RequestedAccountType.Employee), snapshot.Rows[0].RequestedAccountType);
        Assert.NotNull(snapshot.Rows[0].AccountRequestedAtUtc);
        using var applicantSnapshot = await applicant.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, applicantSnapshot.StatusCode);
    }

    // De teller telt altijd Pending, ook wanneer de browser de Approved- of Refused-tab leest.
    [Fact]
    public async Task Approval_refusal_and_reopening_move_rows_versions_and_current_count_in_both_admin_sessions()
    {
        await using var factory = new AccountWebApplicationFactory();
        var firstAdmin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var secondAdmin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var employee = await factory.AddAccountAsync(RequestedAccountType.Employee);
        var external = await factory.AddAccountAsync(RequestedAccountType.ExternalInstructor);
        using var first = factory.CreateIdentityClient();
        using var second = factory.CreateIdentityClient();
        await LoginAdministratorAsync(first, firstAdmin);
        await LoginAdministratorAsync(second, secondAdmin);
        await AssertBothSnapshotsAsync(first, second, AccountApprovalStatus.Pending, 2, [employee.UserId, external.UserId]);
        var originalRows = (await ReadSnapshotAsync(first)).Rows;
        var originalExternal = originalRows.Single(x => x.Id == external.UserId);
        var originalEmployee = originalRows.Single(x => x.Id == employee.UserId);

        await ReviewAsync(first, employee, "Approve", originalEmployee.ConcurrencyStamp!);
        await AssertBothSnapshotsAsync(first, second, AccountApprovalStatus.Pending, 1, [external.UserId]);
        await AssertBothSnapshotsAsync(first, second, AccountApprovalStatus.Approved, 1,
            [firstAdmin.UserId, secondAdmin.UserId, employee.UserId]);
        var approved = (await ReadSnapshotAsync(second, AccountApprovalStatus.Approved)).Rows.Single(x => x.Id == employee.UserId);
        Assert.NotEqual(originalEmployee.ConcurrencyStamp, approved.ConcurrencyStamp);

        await ReviewAsync(second, external, "Refuse", originalExternal.ConcurrencyStamp!, "Aanvulling nodig voor de fictieve aanvraag.");
        await AssertBothSnapshotsAsync(first, second, AccountApprovalStatus.Pending, 0, []);
        await AssertBothSnapshotsAsync(first, second, AccountApprovalStatus.Refused, 0, [external.UserId]);
        var refused = Assert.Single((await ReadSnapshotAsync(first, AccountApprovalStatus.Refused)).Rows);
        Assert.NotEqual(originalExternal.ConcurrencyStamp, refused.ConcurrencyStamp);

        await ReviewAsync(first, external, "Reopen", refused.ConcurrencyStamp!);
        await AssertBothSnapshotsAsync(first, second, AccountApprovalStatus.Pending, 1, [external.UserId]);
        await AssertBothSnapshotsAsync(first, second, AccountApprovalStatus.Refused, 1, []);
        var reopened = Assert.Single((await ReadSnapshotAsync(second)).Rows);
        Assert.Equal(originalExternal.AccountRequestedAtUtc, reopened.AccountRequestedAtUtc);
        Assert.NotEqual(refused.ConcurrencyStamp, reopened.ConcurrencyStamp);
    }

    // Omgekeerde insertvolgorde en gelijke/null-datums mogen pagina's niet laten overlappen of rijen overslaan.
    [Fact]
    public async Task Fifty_row_pages_use_stable_requested_date_then_id_order_including_null_legacy_dates()
    {
        await using var factory = new AccountWebApplicationFactory();
        var admin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var requestedAt = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var applications = Enumerable.Range(0, 53).Select(index => new ApplicationUser
        {
            Id = $"snapshot-order-{index:D3}", UserName = $"order-{index:D3}@example.invalid",
            Email = $"order-{index:D3}@example.invalid", AccountApprovalStatus = AccountApprovalStatus.Pending,
            AccountRequestedAtUtc = index < 3 ? null : requestedAt.AddMinutes((index - 3) / 2),
            ConcurrencyStamp = Guid.NewGuid().ToString(), SecurityStamp = Guid.NewGuid().ToString()
        }).ToArray();
        await factory.WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            db.Users.AddRange(applications.Reverse());
            await db.SaveChangesAsync();
        });
        using var browser = factory.CreateIdentityClient();
        await LoginAdministratorAsync(browser, admin);
        var pageOne = await ReadSnapshotAsync(browser, page: 1);
        var pageTwo = await ReadSnapshotAsync(browser, page: 2);
        var repeated = await ReadSnapshotAsync(browser, page: 1);
        var expected = applications.OrderBy(x => x.AccountRequestedAtUtc).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.Id).ToArray();

        Assert.Equal(53, pageOne.TotalPending);
        Assert.Equal(2, pageOne.TotalPages);
        Assert.Equal(1, pageOne.Page);
        Assert.Equal(2, pageTwo.Page);
        Assert.Equal(50, pageOne.Rows.Count);
        Assert.Equal(3, pageTwo.Rows.Count);
        Assert.Equal(expected.Take(50), pageOne.Rows.Select(x => x.Id));
        Assert.Equal(expected.Skip(50), pageTwo.Rows.Select(x => x.Id));
        Assert.Equal(expected, pageOne.Rows.Concat(pageTwo.Rows).Select(x => x.Id));
        Assert.Equal(pageOne.Rows, repeated.Rows);
        Assert.All(pageOne.Rows.Take(3), row => Assert.Null(row.AccountRequestedAtUtc));
    }

    // Oude onvolledige accounts blijven leesbaar; querygrenzen herstellen naar een geldige bestaande pagina.
    [Fact]
    public async Task Legacy_rows_and_external_organization_remain_safe_with_unknown_status_and_out_of_range_pages()
    {
        await using var factory = new AccountWebApplicationFactory();
        var admin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var legacy = await factory.AddAccountAsync(null);
        var external = await factory.AddAccountAsync(RequestedAccountType.ExternalInstructor);
        using var browser = factory.CreateIdentityClient();
        await LoginAdministratorAsync(browser, admin);
        using var unknown = await browser.GetAjaxAsync("/Admin/AccountApplications/Snapshot?status=999&page=-9");
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        var fallback = (await unknown.Content.ReadFromJsonAsync<AccountApplicationsSnapshot>())!;
        Assert.Equal(nameof(AccountApprovalStatus.Pending), fallback.Status);
        Assert.Equal(1, fallback.Page);
        Assert.Equal(1, fallback.TotalPages);
        Assert.Equal(2, fallback.TotalPending);
        var legacyRow = fallback.Rows.Single(x => x.Id == legacy.UserId);
        Assert.Equal(legacy.Email, legacyRow.DisplayName);
        Assert.Null(legacyRow.RequestedAccountType);
        Assert.Null(legacyRow.DepartmentOrOrganization);
        Assert.Null(legacyRow.AccountRequestedAtUtc);
        var externalRow = fallback.Rows.Single(x => x.Id == external.UserId);
        Assert.Equal("Fictieve HTTP Academie", externalRow.DisplayName);
        Assert.Equal("Fictieve HTTP Academie", externalRow.DepartmentOrOrganization);
        var lastPage = await ReadSnapshotAsync(browser, page: int.MaxValue);
        Assert.Equal(1, lastPage.Page);
        Assert.Equal(fallback.Rows, lastPage.Rows);
    }

    // Een expliciete allowlist bewijst het wire-contract en sluit authsecrets en overbodige profieldata uit.
    [Fact]
    public async Task Snapshot_has_only_the_required_dto_fields_and_disables_browser_and_shared_cache_storage()
    {
        await using var factory = new AccountWebApplicationFactory();
        var admin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var target = await factory.AddAccountAsync(RequestedAccountType.Employee);
        string? passwordHash = null;
        string? securityStamp = null;
        string? expectedVersion = null;
        await factory.WithServicesAsync(async services =>
        {
            var user = await services.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(x => x.Id == target.UserId);
            passwordHash = user.PasswordHash;
            securityStamp = user.SecurityStamp;
            expectedVersion = user.ConcurrencyStamp;
        });
        using var browser = factory.CreateIdentityClient();
        await LoginAdministratorAsync(browser, admin);
        using var response = await browser.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(x => x.Name));
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "page", "rows", "status", "totalPages", "totalPending" },
            document.RootElement.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        var row = Assert.Single(document.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal(new[] { "accountRequestedAtUtc", "concurrencyStamp", "departmentOrOrganization", "displayName", "id", "requestedAccountType", "status" },
            row.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Equal(expectedVersion, row.GetProperty("concurrencyStamp").GetString());
        Assert.False(json.Contains(passwordHash!, StringComparison.Ordinal));
        Assert.False(json.Contains(securityStamp!, StringComparison.Ordinal));
        Assert.False(json.Contains(target.Password, StringComparison.Ordinal));
        Assert.False(json.Contains(target.Email, StringComparison.Ordinal));
    }

    // De opgeslagen rol en stamp worden opnieuw gelezen; oude cookieclaims houden de snapshot niet toegankelijk.
    [Fact]
    public async Task Revoked_administrator_role_and_rotated_security_stamp_reject_existing_snapshot_sessions()
    {
        await using var factory = new AccountWebApplicationFactory();
        var revokedAdmin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var staleAdmin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);
        var target = await factory.AddAccountAsync(RequestedAccountType.Employee);
        using var revokedBrowser = factory.CreateIdentityClient();
        using var staleBrowser = factory.CreateIdentityClient();
        await LoginAdministratorAsync(revokedBrowser, revokedAdmin);
        await LoginAdministratorAsync(staleBrowser, staleAdmin);
        await AssertBothSnapshotsAsync(revokedBrowser, staleBrowser, AccountApprovalStatus.Pending, 1, [target.UserId]);
        await factory.WithServicesAsync(async services =>
        {
            var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var revoked = (await manager.FindByIdAsync(revokedAdmin.UserId))!;
            var stale = (await manager.FindByIdAsync(staleAdmin.UserId))!;
            Assert.True((await manager.RemoveFromRoleAsync(revoked, RoleNames.Beheerder)).Succeeded);
            Assert.True((await manager.UpdateSecurityStampAsync(stale)).Succeeded);
        });
        using var forbidden = await revokedBrowser.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        using var unauthorized = await staleBrowser.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        foreach (var response in new[] { forbidden, unauthorized })
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(target.UserId, body, StringComparison.Ordinal);
        }
        // Een verse login kan alleen de nog bevoegde beheerder opnieuw toelaten.
        await LoginAdministratorAsync(staleBrowser, staleAdmin);
        Assert.Single((await ReadSnapshotAsync(staleBrowser)).Rows);
        await LoginAdministratorAsync(revokedBrowser, revokedAdmin);
        using var stillForbidden = await revokedBrowser.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, stillForbidden.StatusCode);
    }

    /// <summary>Gebruik uitsluitend de normale Razor-login; er wordt geen testprincipal of fake-auth geïnjecteerd.</summary>
    private static async Task LoginAdministratorAsync(IdentityHttpClient browser, TestIdentity admin)
    {
        using var response = await browser.LoginAsync(admin);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>Lees de echte JSON-respons; non-200 mag nooit stilzwijgend als lege lijst tellen.</summary>
    private static async Task<AccountApplicationsSnapshot> ReadSnapshotAsync(IdentityHttpClient browser,
        AccountApprovalStatus status = AccountApprovalStatus.Pending, int page = 1)
    {
        using var response = await browser.GetAjaxAsync($"/Admin/AccountApplications/Snapshot?status={status}&page={page}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountApplicationsSnapshot>())!;
    }

    /// <summary>Twee onafhankelijke cookies moeten dezelfde teller, filter en opgeslagen rijen ontvangen.</summary>
    private static async Task AssertBothSnapshotsAsync(IdentityHttpClient first, IdentityHttpClient second,
        AccountApprovalStatus status, int pendingCount, string[] ids)
    {
        // Twee clients met eigen cookies vragen gelijktijdig dezelfde actuele toestand op.
        var snapshots = await Task.WhenAll(ReadSnapshotAsync(first, status), ReadSnapshotAsync(second, status));
        foreach (var snapshot in snapshots)
        {
            Assert.Equal(pendingCount, snapshot.TotalPending);
            Assert.Equal(status.ToString(), snapshot.Status);
            Assert.Equal(1, snapshot.Page);
            Assert.Equal(1, snapshot.TotalPages);
            Assert.Equal(ids.Order(StringComparer.Ordinal), snapshot.Rows.Select(x => x.Id).Order(StringComparer.Ordinal));
            Assert.All(snapshot.Rows, row => Assert.Equal(status.ToString(), row.Status));
        }
        Assert.Equal(snapshots[0].Rows, snapshots[1].Rows);
    }

    /// <summary>De review gebruikt de oorspronkelijke snapshotversie en een echt formulier-antiforgerytoken.</summary>
    private static async Task ReviewAsync(IdentityHttpClient browser, TestIdentity target, string action,
        string version, string? refusalReason = null)
    {
        var fields = new Dictionary<string, string> { ["ExpectedConcurrencyStamp"] = version };
        if (target.DepartmentId is not null) fields["DepartmentId"] = target.DepartmentId.Value.ToString();
        if (refusalReason is not null) fields["RefusalReason"] = refusalReason;
        using var response = await browser.PostFormAsync($"/Admin/AccountApplications/{target.UserId}/{action}", fields,
            tokenPage: $"/Admin/AccountApplications/{target.UserId}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
