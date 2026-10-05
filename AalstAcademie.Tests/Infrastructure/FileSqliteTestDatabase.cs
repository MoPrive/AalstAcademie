// Sprint 003: Maakt echte SQLite- en Identity-services op één eigen GUID-doel
// Iedere afzonderlijke scope heeft haar eigen context en verbinding voor gelijktijdige writes en onafhankelijke readbacks.
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Elke scope opent een onafhankelijke verbinding naar uitsluitend deze tijdelijke testdatabase.</summary>
public sealed class FileSqliteTestDatabase : IAsyncDisposable
{
    // De operator kiest de testroot; iedere fixture bezit daaronder uitsluitend zijn eigen GUID-map.
    private readonly TemporaryTestDirectory ownedDirectory = new("revision02-services");
    private string directory => ownedDirectory.DirectoryPath;
    private ServiceProvider provider = null!;
    private AsyncServiceScope scope;
    public string DatabasePath => Path.Combine(directory, "accounts.db");
    // Geen pooling: na scope-disposal houdt de provider geen databasebestand open.
    public string ConnectionString => $"Data Source={DatabasePath};Foreign Keys=True;Pooling=False;Default Timeout=2";
    public IServiceProvider Services => scope.ServiceProvider;
    public ApplicationDbContext Context => Services.GetRequiredService<ApplicationDbContext>();
    /// <summary>Een eigen DbContext/verbinding maakt gelijktijdige schrijvers en onafhankelijk commitbewijs mogelijk.</summary>
    public AsyncServiceScope CreateScope() => provider.CreateAsyncScope();

    /// <summary>Bouwt echte SQLite/Identity-services; overrides zijn beperkt tot de bewust geïnjecteerde foutgrens.</summary>
    public static async Task<FileSqliteTestDatabase> CreateAsync(bool migrate = true, IInterceptor? interceptor = null,
        Action<IServiceCollection>? configureServices = null)
    {
        // Sprint 004: SQLite neemt zijn lock synchroon, ook bij een Async-serviceaanroep.
        // Laat de testproces-pool voldoende ruimte voor barrières/commitcontinuaties naast
        // de wachtende schrijvers. Dit verandert geen productiepool, timeout of raceassertion.
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        if (workers < 32 && !ThreadPool.SetMinThreads(32, completionPorts))
            throw new InvalidOperationException("De geïsoleerde provider-races konden geen workerreserve verkrijgen.");
        var database = new FileSqliteTestDatabase();
        Directory.CreateDirectory(database.directory);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlite(database.ConnectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
        });
        services.AddDefaultIdentity<ApplicationUser>(options =>
        {
            options.SignIn.RequireConfirmedAccount = true;
            options.User.RequireUniqueEmail = true;
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddScoped<InstructorProfileService>();
        services.AddScoped<DatabaseInitializer>();
        // De singleton-klok en notifier worden gedeeld tussen alle onafhankelijke scopes van deze testcase.
        services.AddSingleton<FixedTimeProvider>();
        services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FixedTimeProvider>());
        services.AddSingleton<RecordingAccountApplicationsNotifier>();
        services.AddSingleton<IAccountApplicationNotifier>(sp => sp.GetRequiredService<RecordingAccountApplicationsNotifier>());
        services.AddScoped<AccountRegistrationService>();
        services.AddScoped<AccountManagerChoices>();
        services.AddScoped<AccountReviewService>();
        configureServices?.Invoke(services);
        database.provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        database.scope = database.provider.CreateAsyncScope();
        try
        {
            if (migrate) await database.Context.Database.MigrateAsync();
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    /// <summary>Ruimt na scope-disposal uitsluitend de bekende files van deze GUID-testmap op.</summary>
    public async ValueTask DisposeAsync()
    {
        await scope.DisposeAsync();
        await provider.DisposeAsync();
        // Pas na provider-disposal worden de begrensde eigen resources zonder linktraversal verwijderd.
        // Alleen de gevalideerde eigen GUID-map; tijdelijk bezette Windows-files krijgen een begrensde herpoging.
        for (var attempt = 0; ; attempt++)
        {
            try { await ownedDirectory.DisposeAsync(); break; }
            catch (IOException) when (attempt < 4) { await Task.Delay(100 << attempt); }
        }
    }
}
