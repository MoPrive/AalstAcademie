// Sprint 003: Host echte middleware, Identity en Razor met een eigen absoluut databasedoel
// De fixture onderdrukt demoseeds zodat haar gecontroleerde testcasegegevens leidend blijven.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Start de echte applicatie met een eigen SQLite-file, echte Identity en expliciete testconfiguratie.</summary>
public sealed class AccountWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string directory;
    private readonly TemporaryTestDirectory ownedDirectory;
    private readonly string environment;
    private readonly bool demoEnabled;
    private bool cleaned;
    private DemoDatabaseLease? ownedLease;
    // Foutinjecties worden vóór de eerste hostopstart geregistreerd; geen tweede host/reset op dezelfde DB.
    public Action<IServiceCollection>? ConfigureServicesBeforeStart { get; set; }
    // Eén GUID-map per host voorkomt dat parallelle tests elkaar of de draaiende app.db raken.
    public string DatabasePath => Path.Combine(directory, "http-accounts.db");
    public string ConnectionString => $"Data Source={DatabasePath};Foreign Keys=True;Pooling=False;Default Timeout=5";

    /// <summary>Houdt omgeving en demovlag apart zodat ook verboden opstartcombinaties testbaar zijn.</summary>
    public AccountWebApplicationFactory(string environment = "Development", bool demoEnabled = true)
    {
        this.environment = environment;
        this.demoEnabled = demoEnabled;
        ownedDirectory = new TemporaryTestDirectory("revision02-http");
        directory = ownedDirectory.DirectoryPath;
    }

    /// <summary>Stelt de geïsoleerde verbinding in voordat de echte Program-opstart migraties uitvoert.</summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Bij minimal hosting worden deze hostwaarden als argumenten aangeleverd vóór Program leest.
        // Daardoor testen we de echte DemoMode-opstartgrens zonder globale environmentvariabelen te wijzigen.
        var contentRoot = FindContentRoot();
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [HostDefaults.EnvironmentKey] = environment,
            [HostDefaults.ContentRootKey] = contentRoot,
            ["TEST_CONTENTROOT_AALSTACADEMIE_WEB"] = contentRoot,
            ["ConnectionStrings:DefaultConnection"] = ConnectionString,
            ["Demo:Enabled"] = demoEnabled.ToString(),
            ["Demo:ResetTargetPath"] = DatabasePath
        }));
        var host = base.CreateHost(builder);
        var lease = host.Services.GetRequiredService<DemoDatabaseLease>();
        if (!string.Equals(lease.Target.DatabasePath, DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("De HTTP-fixture kan uitsluitend haar eigen lease bewaren.");
        ownedLease = lease;
        return host;
    }

    /// <summary>Vervangt database en klok, terwijl authenticatie en autorisatie productiemiddleware blijven.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseContentRoot(FindContentRoot());
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = ConnectionString,
            ["Demo:Enabled"] = demoEnabled.ToString(),
            ["Demo:ResetTargetPath"] = DatabasePath
        }));
        builder.ConfigureTestServices(services =>
        {
            // Ook cookie-encryptiesleutels horen uitsluitend bij deze eigen GUID-fixture, nooit bij de gebruikersapp.
            services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")))
                .SetApplicationName("AalstAcademie-revision02-http");
            // Verwijder ook de configuratiedelegate: deze mag nooit alsnog de echte app.db verbinden.
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(ConnectionString));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider());
            // De globale demomode blijft echt actief. Alleen de automatische voorbeeldseed wordt
            // voor deze hosts overgeslagen: tests bereiden expliciete accounts voor, zonder consolecredentials.
            services.RemoveAll<DemoDataSeeder>();
            // Ook aanbodseed is testspecifiek uitgeschakeld; echte reset/lease/auth blijven ongewijzigd.
            services.RemoveAll<DemoOfferSeeder>();
            // Sprint 004: deze expliciete accountfixture bouwt ook geen deelnamevoorbeelden op.
            services.RemoveAll<DemoEnrolmentSeeder>();
                services.RemoveAll<DemoParticipationWorkflowSeeder>();
                services.AddScoped(sp => new DemoParticipationWorkflowSeeder(sp.GetRequiredService<ApplicationDbContext>(),
                    new DemoMode(false), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<AalstAcademie.Web.Services.Training.TrainingSchedule>(),
                    sp.GetRequiredService<AalstAcademie.Web.Services.Enrolment.RegistrationReviewService>(),
                    sp.GetRequiredService<AalstAcademie.Web.Services.Enrolment.RegistrationCancellationService>(),
                    sp.GetRequiredService<AalstAcademie.Web.Services.Training.TrainingMomentManagementService>(),
                    sp.GetRequiredService<AalstAcademie.Web.Services.Enrolment.EmployeeEnrolmentService>()));
            services.AddScoped(provider => new DemoEnrolmentSeeder(provider.GetRequiredService<ApplicationDbContext>(),
                new DemoMode(false), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<AalstAcademie.Web.Services.Training.TrainingSchedule>(),
                provider.GetRequiredService<AalstAcademie.Web.Services.Training.TrainingWriteTransaction>(),
                provider.GetRequiredService<AalstAcademie.Web.Services.Enrolment.EmployeeEnrolmentService>()));
            services.AddScoped(provider => new DemoOfferSeeder(provider.GetRequiredService<ApplicationDbContext>(),
                new DemoMode(false), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<AalstAcademie.Web.Services.Training.TrainingSchedule>()));
            services.AddScoped(provider => new DemoDataSeeder(provider.GetRequiredService<ApplicationDbContext>(),
                provider.GetRequiredService<UserManager<ApplicationUser>>(), provider.GetRequiredService<InstructorProfileService>(),
                new DemoMode(false), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<DemoCredentials>()));
            ConfigureServicesBeforeStart?.Invoke(services);
        });
    }

    /// <summary>Een browser bewaart cookies, volgt redirects niet en gebruikt HTTPS voor Secure-cookies.</summary>
    public IdentityHttpClient CreateIdentityClient() => new(CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost")
    }));

    /// <summary>Een aparte DI-scope/verbinding laat tests de werkelijk opgeslagen toestand controleren.</summary>
    public async Task WithServicesAsync(Func<IServiceProvider, Task> operation)
    {
        await using var scope = Services.CreateAsyncScope();
        await operation(scope.ServiceProvider);
    }

    /// <summary>Maakt expliciet fictieve testdata; de publieke HTTP-registratie wordt hierdoor niet nagebootst.</summary>
    public async Task<TestIdentity> AddAccountAsync(RequestedAccountType? type,
        AccountApprovalStatus approval = AccountApprovalStatus.Pending, IEnumerable<string>? roles = null,
        bool blocked = false, bool emailConfirmed = false, string? demoSeedKey = null)
    {
        TestIdentity? identity = null;
        await WithServicesAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var external = type == RequestedAccountType.ExternalInstructor;
            var departmentId = type is null || external ? (int?)null : await db.Departments.Select(x => x.Id).FirstAsync();
            var email = $"http-{Guid.NewGuid():N}@example.invalid";
            var password = NewPassword();
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid().ToString(), Email = email, UserName = email, PhoneNumber = "0490000000",
                RequestedAccountType = type, AccountApprovalStatus = approval, IsBlocked = blocked,
                EmailConfirmed = emailConfirmed, DemoSeedKey = demoSeedKey, DepartmentId = departmentId,
                FirstName = type is null || external ? null : "Noor", LastName = type is null || external ? null : "Peeters",
                ManagerUserId = null,
                AccountRequestedAtUtc = type is null ? null : services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime
            };
            if (!(await manager.CreateAsync(user, password)).Succeeded)
                throw new InvalidOperationException("De fictieve HTTP-testgebruiker kon niet worden voorbereid.");
            // De fixture mag ook geweigerde profielen opbouwen; dit zijn geen publieke privilegebewerkingen.
            if (type == RequestedAccountType.InternalInstructor)
                db.InternalInstructors.Add(new InternalInstructor { ApplicationUserId = user.Id });
            if (external)
                db.ExternalInstructors.Add(new ExternalInstructor { ApplicationUserId = user.Id, OrganizationName = "Fictieve HTTP Academie" });
            await db.SaveChangesAsync();
            if (roles is not null && !(await manager.AddToRolesAsync(user, roles)).Succeeded)
                throw new InvalidOperationException("De fictieve HTTP-testrollen konden niet worden voorbereid.");
            identity = new TestIdentity(user.Id, email, password, departmentId);
        });
        return identity!;
    }

    /// <summary>Maakt tijdelijk sterke testinvoer; alleen Identity bewaart later de bijbehorende hash.</summary>
    public static string NewPassword()
    {
        // Geen herbruikbaar wachtwoord in bron/settings: elke testcase gebruikt nieuwe geheugenwaarden.
        var requiredCategories = new[] { "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz", "0123456789", "!@$%*-_" };
        var random = string.Concat(requiredCategories.Select(category => category[RandomNumberGenerator.GetInt32(category.Length)]));
        return random + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    }

    /// <summary>Sluit eerst host/verbindingen en verwijdert daarna uitsluitend de eigen tijdelijke databasebestanden.</summary>
    public override async ValueTask DisposeAsync()
    {
        // Leg bij succesvolle hostcreatie de eigen lease vast, zonder tijdens disposal een nieuwe host te starten.
        try { await base.DisposeAsync(); }
        finally { ownedLease?.Dispose(); }
        if (cleaned) return;
        for (var attempt = 0; ; attempt++)
        {
            try { await ownedDirectory.DisposeAsync(); break; }
            catch (IOException) when (attempt < 4) { await Task.Delay(100 << attempt); }
        }
        cleaned = true;
    }

    /// <summary>Vindt Razor- en static-file-bronnen ook wanneer de build in een aparte uitvoermap staat.</summary>
    private static string FindContentRoot()
    {
        // Ook aparte build-artifacts en een .slnx-repository werken: zoek de concrete projectmarker.
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory, Path.GetDirectoryName(SourcePath())! })
        {
            for (var candidate = new DirectoryInfo(start); candidate is not null; candidate = candidate.Parent)
            {
                var project = Path.Combine(candidate.FullName, "AalstAcademie.Web");
                if (File.Exists(Path.Combine(project, "AalstAcademie.Web.csproj"))) return project;
            }
        }
        throw new InvalidOperationException("De concrete Web-projectroot voor HTTP-tests werd niet gevonden.");
    }

    // CallerFilePath biedt een compile-time anker; er is geen afhankelijkheid van de draaiende gebruikersapp.
    private static string SourcePath([CallerFilePath] string path = "") => path;
}

/// <summary>Een testidentiteit met uitsluitend tijdelijk gegenereerd wachtwoord; nooit logbaar als plaintext.</summary>
public sealed record TestIdentity(string UserId, string Email, string Password, int? DepartmentId)
{
    // Record.ToString zou anders alle properties, inclusief het tijdelijke wachtwoord, uitschrijven.
    public override string ToString() => "Fictieve HTTP-testidentiteit";
}
