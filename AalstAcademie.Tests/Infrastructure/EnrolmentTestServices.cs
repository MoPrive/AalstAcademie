using AalstAcademie.Web.Services.Accounts;
using AalstAcademie.Web.Services.Enrolment;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Voegt de concrete004-diensten aan bestaande echte SQLite-fixtures toe; geen aparte provider of seed-bypass.</summary>
public static class EnrolmentTestServices
{
    public static void Configure(IServiceCollection services, bool demoEnabled = true)
    {
        TrainingTestServices.Configure(services, demoEnabled);
        services.AddScoped<AccountManagerChoices>();
        services.AddScoped<AccountManagerService>();
        services.AddScoped<DepartmentResponsibleQueries>();
        services.AddScoped<DepartmentResponsibleService>();
        services.AddScoped<EmployeeAccessReader>();
        services.AddScoped<ParticipationEligibility>();
        services.AddScoped<EmployeeCatalogueQueries>();
        services.AddScoped<EmployeeEnrolmentQueries>();
        services.AddScoped<EmployeeEnrolmentService>();
        services.AddScoped<RegistrationManagementQueries>();
        services.AddScoped<RegistrationReviewService>();
        services.AddScoped<RegistrationCancellationService>();
        services.AddScoped<TrainingMomentCancellationService>();
    }
}
