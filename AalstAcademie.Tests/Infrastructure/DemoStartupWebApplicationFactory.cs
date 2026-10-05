// Sprint 003: Start de echte Program-bootstrap met de expliciete fixtureconfiguratie
// Opeenvolgende hosts delen uitsluitend hun eigen database en sleutelring, niet die van de gebruiker.
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
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

/// <summary>Host de echte Program-bootstrap op uitsluitend het gedeelde eigen doel; de eigenaar disposeert de omgeving na alle hosts.</summary>
public sealed class DemoStartupWebApplicationFactory(DemoStartupTestEnvironment testEnvironment,
    bool demoEnabled = true, bool suppressSeed = false, DemoStartupFailureInterceptor? interceptor = null,
    string? resetTargetPath = null) : WebApplicationFactory<Program>
{
    public string DatabasePath => testEnvironment.DatabasePath;
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var settings = testEnvironment.Settings(demoEnabled);
        // Sprint 004: een mismatch moet vóór Program's vroege targetresolutie bestaan, niet pas in late appconfiguratie.
        if (resetTargetPath is not null) settings["Demo:ResetTargetPath"] = resetTargetPath;
        settings[HostDefaults.EnvironmentKey] = testEnvironment.EnvironmentName;
        settings[HostDefaults.ContentRootKey] = testEnvironment.ContentRootPath;
        settings["TEST_CONTENTROOT_AALSTACADEMIE_WEB"] = testEnvironment.ContentRootPath;
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        return base.CreateHost(builder);
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(testEnvironment.EnvironmentName);
        builder.UseContentRoot(testEnvironment.ContentRootPath);
        builder.ConfigureAppConfiguration((_, configuration) => {
            var settings = testEnvironment.Settings(demoEnabled);
            if (resetTargetPath is not null) settings["Demo:ResetTargetPath"] = resetTargetPath;
            configuration.AddInMemoryCollection(settings);
        });
        builder.ConfigureTestServices(services =>
        {
            // Deze override staat vóór de eerste hoststart: geen tweede same-DB-host of reset voor late foutinjectie.
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlite(testEnvironment.ConnectionString);
                if (interceptor is not null) options.AddInterceptors(interceptor);
            });
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(testEnvironment.Clock);
            if (suppressSeed)
            {
                services.RemoveAll<DemoDataSeeder>();
                // Deze testoverride onderdrukt beide volledige batches, nooit de echte bootstrapveiligheid.
                services.RemoveAll<DemoOfferSeeder>();
                // Sprint 004: suppressSeed omvat alle drie batches, met ongewijzigde reset-/lease-/authgrenzen.
                services.RemoveAll<DemoEnrolmentSeeder>();
                services.RemoveAll<DemoParticipationWorkflowSeeder>();
                services.AddScoped(sp => new DemoParticipationWorkflowSeeder(sp.GetRequiredService<ApplicationDbContext>(),
                    new DemoMode(false), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<AalstAcademie.Web.Services.Training.TrainingSchedule>(),
                    sp.GetRequiredService<AalstAcademie.Web.Services.Enrolment.RegistrationReviewService>(),
                    sp.GetRequiredService<AalstAcademie.Web.Services.Enrolment.RegistrationCancellationService>(),
                    sp.GetRequiredService<AalstAcademie.Web.Services.Training.TrainingMomentManagementService>(),
                    sp.GetRequiredService<AalstAcademie.Web.Services.Enrolment.EmployeeEnrolmentService>()));
                services.AddScoped(provider => new DemoEnrolmentSeeder(provider.GetRequiredService<ApplicationDbContext>(),
                    new DemoMode(false), testEnvironment.Clock, provider.GetRequiredService<AalstAcademie.Web.Services.Training.TrainingSchedule>(),
                    provider.GetRequiredService<AalstAcademie.Web.Services.Training.TrainingWriteTransaction>(),
                    provider.GetRequiredService<AalstAcademie.Web.Services.Enrolment.EmployeeEnrolmentService>()));
                services.AddScoped(provider => new DemoOfferSeeder(provider.GetRequiredService<ApplicationDbContext>(),
                    new DemoMode(false), testEnvironment.Clock, provider.GetRequiredService<AalstAcademie.Web.Services.Training.TrainingSchedule>()));
                services.AddScoped(provider => new DemoDataSeeder(provider.GetRequiredService<ApplicationDbContext>(),
                    provider.GetRequiredService<UserManager<ApplicationUser>>(), provider.GetRequiredService<InstructorProfileService>(),
                    new DemoMode(false), testEnvironment.Clock, provider.GetRequiredService<DemoCredentials>()));
            }
            Directory.CreateDirectory(testEnvironment.KeyRingPath);
            services.AddDataProtection().SetApplicationName("AalstAcademie.IsolatedDemo")
                .PersistKeysToFileSystem(new DirectoryInfo(testEnvironment.KeyRingPath));
        });
    }
    public IdentityHttpClient CreateIdentityClient() => new(CreateClient(new WebApplicationFactoryClientOptions
    { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") }));
    public async Task WithServicesAsync(Func<IServiceProvider, Task> operation)
    {
        await using var scope = Services.CreateAsyncScope();
        await operation(scope.ServiceProvider);
    }
}
