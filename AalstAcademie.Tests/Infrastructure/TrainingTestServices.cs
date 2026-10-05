// Sprint 003: Registreert opleidings-, moment-, zaal- en foundationdiensten in bestaande geïsoleerde fixtures
// De vervangbare testklok maakt tijdgrenzen reproduceerbaar.
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Voegt uitsluitend trainingsservices toe aan bestaande geïsoleerde fixtures; geen productie-DI of seed.</summary>
public static class TrainingTestServices
{
    public static void Configure(IServiceCollection services, bool demoEnabled = true)
    {
        // De fixture bezit klok en SQLite-verbinding. Alleen de effectieve demogrens wordt bewust gekozen.
        services.RemoveAll<DemoMode>();
        services.AddSingleton(new DemoMode(demoEnabled));
        services.AddScoped<EmployeeAccessReader>();
        services.AddScoped<ParticipationEligibility>();
        services.AddScoped<WaitlistPromotionService>();
        services.AddScoped<TrainingAccessReader>();
        services.AddScoped<TrainingWriteTransaction>();
        services.AddScoped(provider => new TrainingSchedule(provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<TrainingValueValidation>();
        services.AddScoped<TrainingManagementService>();
        services.AddScoped<CategoryManagementService>();
        services.AddScoped<TrainingManagementQueries>();
        // Foundationqueries lezen echte historische rijen; geen dummybezetting of toekomstige flowservice.
        services.AddScoped<TrainingFoundationQueries>();
        services.AddScoped<TrainingMomentManagementService>();
        services.AddScoped<TrainingMomentManagementQueries>();
        services.AddScoped<LocationManagementService>();
        services.AddScoped<LocationManagementQueries>();
    }
}
