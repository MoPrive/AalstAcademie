// Sprint 003: Bezit één tijdelijke databaseomgeving, vaste testklok en eigen DataProtection-keyring voor opeenvolgende hosts
// Zo zijn restart en oude cookies onafhankelijk van de gebruikersapp toetsbaar.
using System.Runtime.CompilerServices;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Eén expliciet tijdelijk doel voor bootstrap, opeenvolgende hosts, klok en eigen DataProtection-keyring.</summary>
public sealed class DemoStartupTestEnvironment : IHostEnvironment, IDisposable, IAsyncDisposable
{
    private readonly TemporaryTestDirectory directory = new("demo-startup");
    public string DatabasePath => directory.CreatePath("demo.db");
    public string KeyRingPath => directory.CreatePath("keys");
    public string ConnectionString => $"Data Source={DatabasePath};Foreign Keys=True;Pooling=False;Default Timeout=5";
    public FixedTimeProvider Clock { get; } = new();
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "AalstAcademie.Tests";
    public string ContentRootPath { get; set; } = FindWebContentRoot();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

    public IConfiguration Configuration(bool demoEnabled = true) => new ConfigurationBuilder().AddInMemoryCollection(Settings(demoEnabled)).Build();
    public Dictionary<string, string?> Settings(bool demoEnabled = true) => new()
    {
        ["ConnectionStrings:DefaultConnection"] = ConnectionString,
        ["Demo:ResetTargetPath"] = DatabasePath,
        ["Demo:Enabled"] = demoEnabled.ToString()
    };

    /// <summary>Echte SQLite/Identity voor bootstrap-unitbewijzen; deze helper start geen HTTP-host.</summary>
    public ServiceProvider CreateServices(bool demoEnabled = true, IInterceptor? interceptor = null, bool suppressSeed = false, bool suppressWorkflowSeed = false)
    {
        var mode = DemoMode.From(this, Configuration(demoEnabled));
        var target = DemoDatabaseTarget.From(this, Configuration(demoEnabled), mode);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mode);
        services.AddSingleton(target);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<DemoCredentials>();
        services.AddSingleton(provider => DemoDatabaseLease.Acquire(provider.GetRequiredService<DemoDatabaseTarget>()));
        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseSqlite(target.ConnectionString);
            if (interceptor is not null) options.AddInterceptors(interceptor);
        });
        services.AddDefaultIdentity<ApplicationUser>(options =>
        {
            options.SignIn.RequireConfirmedAccount = !mode.IsEnabled;
            options.User.RequireUniqueEmail = true;
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddScoped<InstructorProfileService>();
        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<TrainingSchedule>(provider => new(provider.GetRequiredService<TimeProvider>()));
        services.AddScoped(provider => new DemoDataSeeder(provider.GetRequiredService<ApplicationDbContext>(),
            provider.GetRequiredService<UserManager<ApplicationUser>>(), provider.GetRequiredService<InstructorProfileService>(),
            suppressSeed ? new DemoMode(false) : mode, Clock, provider.GetRequiredService<DemoCredentials>()));
        services.AddScoped<AalstAcademie.Web.Services.Training.TrainingAccessReader>();
        services.AddScoped<AalstAcademie.Web.Services.Training.TrainingWriteTransaction>();
        services.AddScoped<AalstAcademie.Web.Services.Training.TrainingValueValidation>();
        services.AddScoped<AalstAcademie.Web.Services.Training.TrainingFoundationQueries>();
        services.AddScoped<AalstAcademie.Web.Services.Training.TrainingMomentManagementService>();
        services.AddScoped<AalstAcademie.Web.Services.Enrolment.EmployeeAccessReader>();
        services.AddScoped<AalstAcademie.Web.Services.Enrolment.ParticipationEligibility>();
        services.AddScoped<AalstAcademie.Web.Services.Enrolment.WaitlistPromotionService>();
        services.AddScoped<AalstAcademie.Web.Services.Enrolment.RegistrationReviewService>();
        services.AddScoped<AalstAcademie.Web.Services.Enrolment.RegistrationCancellationService>();
        services.AddScoped<AalstAcademie.Web.Services.Enrolment.EmployeeEnrolmentService>();
        services.AddScoped(sp => new DemoParticipationWorkflowSeeder(sp.GetRequiredService<ApplicationDbContext>(),
            suppressWorkflowSeed || suppressSeed ? new DemoMode(false) : mode, Clock, sp.GetRequiredService<TrainingSchedule>(),
            sp.GetRequiredService<AalstAcademie.Web.Services.Enrolment.RegistrationReviewService>(),
            sp.GetRequiredService<AalstAcademie.Web.Services.Enrolment.RegistrationCancellationService>(),
            sp.GetRequiredService<TrainingMomentManagementService>(), sp.GetRequiredService<AalstAcademie.Web.Services.Enrolment.EmployeeEnrolmentService>()));
        services.AddScoped<DemoDatabaseBootstrapper>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Nieuwe kopieën gebruiken hun eigen concrete projectroot; CallerFilePath is slechts een bronanker.</summary>
    private static string FindWebContentRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory, Path.GetDirectoryName(SourcePath())! })
            for (var candidate = new DirectoryInfo(start); candidate is not null; candidate = candidate.Parent)
            {
                var project = Path.Combine(candidate.FullName, "AalstAcademie.Web");
                if (File.Exists(Path.Combine(project, "AalstAcademie.Web.csproj"))) return project;
            }
        throw new InvalidOperationException("De geïsoleerde concrete Web-projectroot werd niet gevonden.");
    }
    private static string SourcePath([CallerFilePath] string path = "") => path;
    public void Dispose() => directory.Dispose();
    public ValueTask DisposeAsync() => directory.DisposeAsync();
}
