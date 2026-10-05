using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>
/// Geïsoleerde SQLite-testomgeving met echte Identity/EF-services en async opruiming.
/// Elke CreateAsync-aanroep krijgt een eigen lege geheugendatabase.
/// </summary>
public sealed class SqliteTestDatabase : IAsyncDisposable
{
    // Een SQLite-geheugendatabase leeft zolang deze verbinding open blijft.
    // Foreign Keys=True laat de database de relatieconstraints daadwerkelijk afdwingen.
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    // null! markeert initialisatie in CreateAsync; de factory vult provider vóór gebruik in.
    private ServiceProvider provider = null!;
    private AsyncServiceScope scope;
    // Eén scope deelt dezelfde DbContext tussen de test en de opgevraagde services.
    public IServiceProvider Services => scope.ServiceProvider;
    public ApplicationDbContext Context => Services.GetRequiredService<ApplicationDbContext>();

    /// <summary>
    /// Bouwt de testservices op zoals de applicatie. migrate=false laat upgrade-tests
    /// zelf de eerste migratie kiezen; een interceptor kan een opslagconflict injecteren.
    /// </summary>
    public static async Task<SqliteTestDatabase> CreateAsync(bool migrate = true, IInterceptor? interceptor = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var database = new SqliteTestDatabase();
        await database.connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlite(database.connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
        });
        // Gebruik echte managers/stores; er wordt geen mock- of EF InMemory-provider gebruikt.
        services.AddDefaultIdentity<ApplicationUser>(options =>
        {
            options.SignIn.RequireConfirmedAccount = true;
            options.User.RequireUniqueEmail = true;
        })
            .AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddScoped<InstructorProfileService>();
        services.AddScoped<DatabaseInitializer>();
        // Sprint 002 voegt registratie/beoordeling toe aan de bestaande foundation-fixture.
        // Dezelfde deterministische klok en opname-notifier staan ter beschikking van test en service.
        services.AddSingleton<FixedTimeProvider>();
        services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FixedTimeProvider>());
        services.AddSingleton<RecordingAccountApplicationsNotifier>();
        services.AddSingleton<IAccountApplicationNotifier>(sp => sp.GetRequiredService<RecordingAccountApplicationsNotifier>());
        // Beide services delen de scoped DbContext: user, profiel, rollen en reviewvelden kunnen samen rollbacken.
        services.AddScoped<AccountRegistrationService>();
        services.AddScoped<AccountManagerChoices>();
        services.AddScoped<AccountReviewService>();
        // Foutinjectie vervangt hoogstens de expliciet gekozen service; echte SQLite blijft behouden.
        configureServices?.Invoke(services);
        // ValidateScopes signaleert verkeerd gebruik van scoped services buiten hun levensduur.
        database.provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        database.scope = database.provider.CreateAsyncScope();
        if (migrate) await database.Context.Database.MigrateAsync();
        return database;
    }

    /// <summary>Voegt fictieve accountdata toe om services te testen; kent geen rollen toe.</summary>
    public async Task<ApplicationUser> AddUserAsync(RequestedAccountType? type,
        AccountApprovalStatus approval = AccountApprovalStatus.Pending, bool blocked = false)
    {
        var external = type == RequestedAccountType.ExternalInstructor;
        // example.invalid is testdata; unieke namen voorkomen botsingen binnen een testcase.
        var email = $"demo-{Guid.NewGuid():N}@example.invalid";
        var department = new Department { Name = $"Afdeling {Guid.NewGuid():N}" };
        if (!external) Context.Departments.Add(department);
        var user = new ApplicationUser
        {
            RequestedAccountType = type, AccountApprovalStatus = approval, IsBlocked = blocked,
            FirstName = external ? null : "Noor", LastName = external ? null : "Peeters",
            Department = external ? null : department,
            ManagerUserId = null,
            UserName = email, NormalizedUserName = email.ToUpperInvariant(),
            Email = email, NormalizedEmail = email.ToUpperInvariant(), PhoneNumber = "0490000000"
        };
        // Rechtstreekse EF-opslag maakt ook bewust ongeldige/legacy typen testbaar.
        // Dit is testvoorbereiding, geen vervanging voor toekomstige publieke registratie.
        Context.Users.Add(user);
        await Context.SaveChangesAsync();
        return user;
    }

    /// <summary>Ruimt eerst de services op en sluit daarna de verbinding en geheugendatabase.</summary>
    public async ValueTask DisposeAsync()
    {
        await scope.DisposeAsync();
        await provider.DisposeAsync();
        await connection.DisposeAsync();
    }
}
