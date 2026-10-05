using System.Net;
using System.Reflection;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Hubs;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Realtime;

/// <summary>Echte SignalR-transporttests met Identity-cookies en uitsluitend een eigen tijdelijke SQLite-host.</summary>
public sealed class AccountApplicationsHubHttpTests
{
    private static readonly TimeSpan EventDeadline = TimeSpan.FromSeconds(10);

    // LongPolling loopt door de echte negotiate/connect-middleware. Dit bewijst het transport;
    // browserbediening en herstel van de gebruikersinterface worden afzonderlijk gecontroleerd.
    [Fact]
    public async Task Two_administrators_receive_empty_event_after_real_committed_review()
    {
        await using var factory = new AccountWebApplicationFactory();
        var firstAdmin = await AddAdministratorAsync(factory);
        var secondAdmin = await AddAdministratorAsync(factory);
        var applicant = await factory.AddAccountAsync(RequestedAccountType.Employee);
        using var firstBrowser = factory.CreateIdentityClient();
        using var secondBrowser = factory.CreateIdentityClient();
        var firstCookie = await LoginCookieAsync(firstBrowser, firstAdmin);
        var secondCookie = await LoginCookieAsync(secondBrowser, secondAdmin);
        await using var firstConnection = CreateConnection(factory, firstCookie);
        await using var secondConnection = CreateConnection(factory, secondCookie);

        // Elke operatie krijgt nieuwe completion-bronnen en handlers. Een eerder event kan
        // daardoor nooit de volgende overgang ten onrechte groen maken. Beide beheerders
        // behouden dezelfde echte cookieverbinding voor alle vier opgeslagen wijzigingen.
        async Task ObserveBothAsync(Func<Task> change, Func<IServiceProvider, Task> assertStored, bool startConnections = false)
        {
            var firstEvent = EmptyEventCompletion();
            var secondEvent = EmptyEventCompletion();
            using var firstHandler = RecordNextEvent(firstConnection, firstEvent);
            using var secondHandler = RecordNextEvent(secondConnection, secondEvent);
            if (startConnections)
            {
                await StartAsync(firstConnection);
                await StartAsync(secondConnection);
            }
            Assert.Equal(HubConnectionState.Connected, firstConnection.State);
            Assert.Equal(HubConnectionState.Connected, secondConnection.State);
            await change();
            Assert.Empty(await firstEvent.Task.WaitAsync(EventDeadline));
            Assert.Empty(await secondEvent.Task.WaitAsync(EventDeadline));
            // Een onafhankelijke scope/verbinding ziet de duurzame toestand voordat de volgende
            // overgang begint; louter een in-memory publicatie is geen acceptatiebewijs.
            await factory.WithServicesAsync(assertStored);
            Assert.Equal(HubConnectionState.Connected, firstConnection.State);
            Assert.Equal(HubConnectionState.Connected, secondConnection.State);
        }

        // Approve gebruikt dezelfde HTTP-route, CSRF en oorspronkelijke reviewversie als de UI.
        await ObserveBothAsync(async () =>
        {
            using var response = await firstBrowser.PostFormAsync($"/Admin/AccountApplications/{applicant.UserId}/Approve",
                await ReviewFieldsAsync(factory, applicant), tokenPage: $"/Admin/AccountApplications/{applicant.UserId}");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }, async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == applicant.UserId);
            Assert.Equal(AccountApprovalStatus.Approved, user.AccountApprovalStatus);
            Assert.NotNull(user.ReviewedAt);
            Assert.Equal(firstAdmin.UserId, user.ReviewedById);
            Assert.Equal(new[] { RoleNames.Medewerker }, await services.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(user));
        }, startConnections: true);

        using var registeringBrowser = factory.CreateIdentityClient();
        var email = $"hub-aanvraag-{Guid.NewGuid():N}@example.invalid";
        var password = AccountWebApplicationFactory.NewPassword();
        var form = new Dictionary<string, string>
        {
            ["Input.RequestedAccountType"] = ((int)RequestedAccountType.Employee).ToString(),
            ["Input.Email"] = email, ["Input.PhoneNumber"] = "0490000000",
            ["Input.Password"] = password, ["Input.ConfirmPassword"] = password,
            ["Input.FirstName"] = "Noor", ["Input.LastName"] = "Peeters",
            ["Input.DepartmentId"] = applicant.DepartmentId!.Value.ToString(), ["Input.ManagerUserId"] = ""
        };
        TestIdentity? registered = null;
        DateTime? requestedAt = null;
        string? previousVersion = null;
        // Een derde, aanvankelijk anonieme browser dient de echte publieke aanvraag in.
        await ObserveBothAsync(async () =>
        {
            using var response = await registeringBrowser.PostFormAsync("/Identity/Account/Register", form);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/Account/Status", response.Headers.Location?.OriginalString);
        }, async services =>
        {
            var user = await services.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(x => x.Email == email);
            Assert.Equal(AccountApprovalStatus.Pending, user.AccountApprovalStatus);
            Assert.False(user.IsBlocked);
            Assert.False(user.EmailConfirmed);
            Assert.NotNull(user.AccountRequestedAtUtc);
            Assert.Empty(await services.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(user));
            registered = new TestIdentity(user.Id, email, password, user.DepartmentId);
            requestedAt = user.AccountRequestedAtUtc;
            previousVersion = user.ConcurrencyStamp;
        });

        Assert.NotNull(registered);
        var reviewApplicant = registered!;
        const string refusalReason = "Fictieve realtime-controle vraagt aanvulling.";
        await ObserveBothAsync(async () =>
        {
            var fields = await ReviewFieldsAsync(factory, reviewApplicant);
            fields["RefusalReason"] = refusalReason;
            using var response = await firstBrowser.PostFormAsync($"/Admin/AccountApplications/{reviewApplicant.UserId}/Refuse", fields,
                tokenPage: $"/Admin/AccountApplications/{reviewApplicant.UserId}");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }, async services =>
        {
            var user = await services.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(x => x.Id == reviewApplicant.UserId);
            Assert.Equal(AccountApprovalStatus.Refused, user.AccountApprovalStatus);
            Assert.Equal(refusalReason, user.RefusalReason);
            Assert.Equal(firstAdmin.UserId, user.ReviewedById);
            Assert.NotNull(user.ReviewedAt);
            Assert.Equal(requestedAt, user.AccountRequestedAtUtc);
            Assert.NotEqual(previousVersion, user.ConcurrencyStamp);
            Assert.Empty(await services.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(user));
            previousVersion = user.ConcurrencyStamp;
        });

        // De tweede beheerder heropent met een vers gelezen reviewversie; beide verbindingen
        // moeten ook dit vierde payload-loze event ontvangen zonder opnieuw te verbinden.
        await ObserveBothAsync(async () =>
        {
            using var response = await secondBrowser.PostFormAsync($"/Admin/AccountApplications/{reviewApplicant.UserId}/Reopen",
                await ReviewFieldsAsync(factory, reviewApplicant), tokenPage: $"/Admin/AccountApplications/{reviewApplicant.UserId}");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }, async services =>
        {
            var user = await services.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(x => x.Id == reviewApplicant.UserId);
            Assert.Equal(AccountApprovalStatus.Pending, user.AccountApprovalStatus);
            Assert.Null(user.RefusalReason);
            Assert.Equal(secondAdmin.UserId, user.ReviewedById);
            Assert.NotNull(user.ReviewedAt);
            Assert.Equal(requestedAt, user.AccountRequestedAtUtc);
            Assert.NotEqual(previousVersion, user.ConcurrencyStamp);
            Assert.Empty(await services.GetRequiredService<UserManager<ApplicationUser>>().GetRolesAsync(user));
        });
    }

    // Zeven afzonderlijke toestanden bewijzen dat authenticatie of een deelnemerrol geen hubrecht verleent.
    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("pending", HttpStatusCode.Forbidden)]
    [InlineData("refused", HttpStatusCode.Forbidden)]
    [InlineData("employee", HttpStatusCode.Forbidden)]
    [InlineData("internal", HttpStatusCode.Forbidden)]
    [InlineData("external", HttpStatusCode.Forbidden)]
    [InlineData("blocked", HttpStatusCode.Unauthorized)]
    public async Task Ineligible_account_cannot_negotiate_or_connect(string accountState, HttpStatusCode expected)
    {
        await using var factory = new AccountWebApplicationFactory();
        using var browser = factory.CreateIdentityClient();
        string? cookie = null;
        if (accountState != "anonymous")
        {
            var type = accountState == "external" ? RequestedAccountType.ExternalInstructor
                : accountState == "internal" ? RequestedAccountType.InternalInstructor : RequestedAccountType.Employee;
            var approval = accountState == "pending" ? AccountApprovalStatus.Pending
                : accountState == "refused" ? AccountApprovalStatus.Refused : AccountApprovalStatus.Approved;
            var roles = accountState == "blocked" ? new[] { RoleNames.Beheerder }
                : approval == AccountApprovalStatus.Approved ? RoleNames.ForApprovedAccountType(type).ToArray() : [];
            var identity = await factory.AddAccountAsync(type, approval, roles);
            cookie = await LoginCookieAsync(browser, identity);
            if (accountState == "blocked")
            {
                // Eerst een geldige echte cookie, daarna blokkeren: de opgeslagen toestand wint.
                await factory.WithServicesAsync(async services =>
                {
                    var db = services.GetRequiredService<ApplicationDbContext>();
                    await db.Users.Where(x => x.Id == identity.UserId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsBlocked, true));
                });
            }
        }
        await using var connection = CreateConnection(factory, cookie);
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => StartAsync(connection));
        Assert.Equal(expected, failure.StatusCode);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    // De hub is uitsluitend een ontvangkanaal: beoordeling blijft een beveiligde HTTP-POST met CSRF en versiecontrole.
    [Fact]
    public async Task Connected_administrator_has_no_public_client_group_or_mutation_methods()
    {
        await using var factory = new AccountWebApplicationFactory();
        var administrator = await AddAdministratorAsync(factory);
        var applicant = await factory.AddAccountAsync(RequestedAccountType.Employee);
        using var browser = factory.CreateIdentityClient();
        await using var connection = CreateConnection(factory, await LoginCookieAsync(browser, administrator));
        await StartAsync(connection);
        // De hub declareert geen publiek callable methodes. Beide rechtstreekse protocollaire
        // pogingen worden bovendien door de echte server afgewezen, niet alleen door een verborgen UI.
        Assert.Empty(typeof(AccountApplicationsHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinGroup", "Beheerder").WaitAsync(EventDeadline));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Approve", applicant.UserId).WaitAsync(EventDeadline));
        await factory.WithServicesAsync(async services =>
        {
            var user = await services.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(x => x.Id == applicant.UserId);
            Assert.Equal(AccountApprovalStatus.Pending, user.AccountApprovalStatus);
        });
    }

    // Ontvangers worden per publicatie opnieuw bepaald; een bestaande verbinding bewaart geen blijvend beheerrecht.
    [Fact]
    public async Task Publication_excludes_currently_revoked_administrator_even_with_existing_connection()
    {
        await using var factory = new AccountWebApplicationFactory();
        var remainingAdministrator = await AddAdministratorAsync(factory);
        var revokedAdministrator = await AddAdministratorAsync(factory);
        using var remainingBrowser = factory.CreateIdentityClient();
        using var revokedBrowser = factory.CreateIdentityClient();
        await using var remainingConnection = CreateConnection(factory, await LoginCookieAsync(remainingBrowser, remainingAdministrator));
        await using var revokedConnection = CreateConnection(factory, await LoginCookieAsync(revokedBrowser, revokedAdministrator));
        var initialRemainingEvent = EmptyEventCompletion();
        var initialRevokedEvent = EmptyEventCompletion();
        using var firstRemainingHandler = RecordNextEvent(remainingConnection, initialRemainingEvent);
        using var firstRevokedHandler = RecordNextEvent(revokedConnection, initialRevokedEvent);
        await StartAsync(remainingConnection);
        await StartAsync(revokedConnection);
        // Beide verbindingen moeten eerst aantoonbaar echt werken: een nooit verbonden ontvanger
        // kan anders ten onrechte een groen negatief resultaat geven.
        await PublishAsync(factory);
        Assert.Empty(await initialRemainingEvent.Task.WaitAsync(EventDeadline));
        Assert.Empty(await initialRevokedEvent.Task.WaitAsync(EventDeadline));
        firstRemainingHandler.Dispose();
        firstRevokedHandler.Dispose();
        var remainingEvent = EmptyEventCompletion();
        var excludedEvent = EmptyEventCompletion();
        using var remainingHandler = RecordNextEvent(remainingConnection, remainingEvent);
        using var excludedHandler = RecordNextEvent(revokedConnection, excludedEvent);
        await factory.WithServicesAsync(async services =>
        {
            // Rechtstreeks verwijderen behoudt expres de oudere rolclaim/stamp in de cookie.
            // Publicatie en snapshot mogen deze claim niet als actuele bevoegdheid vertrouwen.
            var db = services.GetRequiredService<ApplicationDbContext>();
            await db.UserRoles.Where(x => x.UserId == revokedAdministrator.UserId).ExecuteDeleteAsync();
        });
        await PublishAsync(factory);
        Assert.Empty(await remainingEvent.Task.WaitAsync(EventDeadline));
        // Het positieve event is een transportbarrière. Een begrensd stil venster bewijst dat
        // de ingetrokken ontvanger deze publicatie niet krijgt; dit claimt geen algemene directe disconnect.
        await Assert.ThrowsAsync<TimeoutException>(() => excludedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        using var deniedSnapshot = await revokedBrowser.GetAjaxAsync("/Admin/AccountApplications/Snapshot");
        Assert.Equal(HttpStatusCode.Forbidden, deniedSnapshot.StatusCode);
    }

    // Een oudere echte cookie moet reeds bij negotiate/connect falen na sessie-intrekking.
    [Fact]
    public async Task Original_cookie_cannot_connect_after_its_security_stamp_is_rotated()
    {
        await using var factory = new AccountWebApplicationFactory();
        var administrator = await AddAdministratorAsync(factory);
        using var browser = factory.CreateIdentityClient();
        var originalCookie = await LoginCookieAsync(browser, administrator);
        await factory.WithServicesAsync(async services =>
        {
            var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await manager.FindByIdAsync(administrator.UserId))!;
            Assert.True((await manager.UpdateSecurityStampAsync(user)).Succeeded);
        });
        await using var connection = CreateConnection(factory, originalCookie);
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => StartAsync(connection));
        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
        // De afwijzing is sessiegebonden: opnieuw geldig aanmelden kan nog steeds verbinden.
        await using var freshConnection = CreateConnection(factory, await LoginCookieAsync(browser, administrator));
        await StartAsync(freshConnection);
        Assert.Equal(HubConnectionState.Connected, freshConnection.State);
    }

    /// <summary>Maakt alleen fictieve beheerders in de geïsoleerde host, buiten publieke registratie.</summary>
    private static Task<TestIdentity> AddAdministratorAsync(AccountWebApplicationFactory factory) =>
        factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]);

    /// <summary>Haalt de authenticatiecookie uit de echte wachtwoordlogin voor gebruik door de SignalR-client.</summary>
    private static async Task<string> LoginCookieAsync(IdentityHttpClient browser, TestIdentity identity)
    {
        using var login = await browser.LoginAsync(identity);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.True(login.Headers.TryGetValues("Set-Cookie", out var values));
        // Neem alle Identity-cookiechunks mee. Cookie-attributen en antiforgerycookies zijn geen
        // authenticatieheader; nooit een eigen testprincipal of bearer-token fabriceren.
        var cookies = values!.Select(value => value.Split(';', 2)[0])
            .Where(value => value.StartsWith(".AspNetCore.Identity.Application", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(cookies);
        return string.Join("; ", cookies);
    }

    /// <summary>LongPolling gebruikt TestServer HTTP; dit bewijs claimt geen WebSocket-transport of echte browser-DOM.</summary>
    private static HubConnection CreateConnection(AccountWebApplicationFactory factory, string? cookie) =>
        new HubConnectionBuilder().WithUrl("https://localhost/hubs/account-applications", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            if (cookie is not null) options.Headers.Add("Cookie", cookie);
        }).Build();

    /// <summary>Een begrensde deadline laat een kapotte verbinding falen zonder onbeperkte testwachttijd.</summary>
    private static Task StartAsync(HubConnection connection) => connection.StartAsync().WaitAsync(EventDeadline);

    /// <summary>Asynchrone continuations voorkomen dat assertions op de interne transportcallback uitvoeren.</summary>
    private static TaskCompletionSource<object?[]> EmptyEventCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Een tijdelijk abonnement observeert het volgende payload-loze invalidatiesignaal.</summary>
    private static IDisposable RecordNextEvent(HubConnection connection, TaskCompletionSource<object?[]> completion) =>
        connection.On("AccountApplicationsChanged", Type.EmptyTypes, arguments =>
        {
            // De geregistreerde payload-loze protocolhandler accepteert geen persoonsgegevens.
            completion.TrySetResult(arguments);
            return Task.CompletedTask;
        });

    /// <summary>Roept de echte ontvangerselectie/notifier aan; de verbindingen en protocollen blijven echte SignalR.</summary>
    private static Task PublishAsync(AccountWebApplicationFactory factory) => factory.WithServicesAsync(services =>
        services.GetRequiredService<IAccountApplicationNotifier>().NotifyChangedAsync());

    /// <summary>Leest bewust een nieuwe versie voor elke volgende overgang, zonder de eerdere conflictproef te omzeilen.</summary>
    private static async Task<Dictionary<string, string>> ReviewFieldsAsync(AccountWebApplicationFactory factory, TestIdentity identity)
    {
        var fields = new Dictionary<string, string>();
        await factory.WithServicesAsync(async services =>
        {
            var user = await services.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(x => x.Id == identity.UserId);
            fields["ExpectedConcurrencyStamp"] = user.ConcurrencyStamp!;
            if (user.DepartmentId is not null) fields["DepartmentId"] = user.DepartmentId.Value.ToString();
        });
        return fields;
    }
}
