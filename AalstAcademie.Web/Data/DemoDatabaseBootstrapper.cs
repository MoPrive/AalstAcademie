// Sprint 003: Coördineert gevalideerd doel, lease, effectieve demo-reset, schema, referenties, accounts en aanbod vóór HTTP
// Iedere startupfout wist de hint en stopt het opstarten.
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Services.Training;
using AalstAcademie.Web.Services.Enrolment;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Data;

/// <summary>Voltooit schema, referenties, accounts én aanbod voordat Program HTTP of aanmeldhints vrijgeeft.</summary>
public sealed class DemoDatabaseBootstrapper(ApplicationDbContext context, DatabaseInitializer initializer,
    DemoDataSeeder seeder, DemoMode mode, DemoDatabaseTarget target, DemoDatabaseLease lease,
    DemoCredentials credentials, TrainingSchedule schedule, TimeProvider clock, DemoOfferSeeder? offers = null,
    DemoEnrolmentSeeder? enrolments = null, DemoParticipationWorkflowSeeder? workflow = null)
{
    private bool attempted;
    // De host roept dit vóór Run aan: een exception voorkomt HTTP-bereikbaarheid en laat geen bruikbare tijdelijke hint achter.
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (attempted) throw new InvalidOperationException("Een host voert bootstrap slechts eenmaal uit.");
        attempted = true;
        credentials.Clear();
        try
        {
            // Program resolveert zone en lifetimelease vóór deze scoped operatie.
            _ = schedule.TimeZone;
            if (!context.Database.IsSqlite() || !DemoDatabaseTarget.PathEquals(lease.Target.DatabasePath, target.DatabasePath))
                throw new InvalidOperationException("Bootstrap vereist dezelfde gevalideerde SQLite-provider en applicatielease.");
            var actual = new SqliteConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString);
            if (!DemoDatabaseTarget.PathEquals(DemoDatabaseTarget.CanonicalPath(actual.DataSource, Path.GetDirectoryName(target.DatabasePath)!), target.DatabasePath))
                throw new InvalidOperationException("De geïnjecteerde verbinding verschilt van het gevalideerde bootstrapdoel.");
            if (mode.IsEnabled)
            {
                lease.ProbeResetFiles();
                // Alleen de provider verwijdert haar exacte bestanden; geen directory/globdelete.
                // Dit is het expliciete volledige D78-demoresetcontract. Non-demo passeert deze tak; tests kiezen uitsluitend hun eigen doel.
                await context.Database.EnsureDeletedAsync(cancellationToken);
                context.ChangeTracker.Clear();
            }
            await initializer.InitializeAsync(cancellationToken);
            if (!mode.IsEnabled) return;
            var result = await seeder.SeedAsync(cancellationToken);
            // Test-only seedoverride behoudt echte reset/schema/auth maar publiceert geen hint.
            if (!result.IsSeeded) return;
            // De fallback gebruikt dezelfde scoped context/klok voor bestaande bootstrap-unitfixtures.
            // Program registreert de dienst expliciet; een incomplete aanbodbatch publiceert geen credentials.
            await (offers ?? new DemoOfferSeeder(context, mode, clock, schedule)).SeedAsync(cancellationToken);
            // Bestaande unitfixtures krijgen dezelfde normale scoped diensten als Program; geen andere opslag of klok.
            var featureTransaction = new TrainingWriteTransaction(context);
            var featureAccess = new EmployeeAccessReader(context, mode);
            var featureEligibility = new ParticipationEligibility(context, schedule, mode);
            await (enrolments ?? new DemoEnrolmentSeeder(context, mode, clock, schedule, featureTransaction,
                new EmployeeEnrolmentService(context, featureAccess, featureEligibility, featureTransaction, clock, new WaitlistPromotionService(context, featureEligibility, schedule, clock)))).SeedAsync(cancellationToken);
            var promotion = new WaitlistPromotionService(context, featureEligibility, schedule, clock);
            var trainingAccess = new TrainingAccessReader(context, mode);
            await (workflow ?? new DemoParticipationWorkflowSeeder(context, mode, clock, schedule,
                new RegistrationReviewService(context, trainingAccess, featureTransaction, schedule, promotion),
                new RegistrationCancellationService(context, featureAccess, featureTransaction, schedule, promotion),
                new TrainingMomentManagementService(context, trainingAccess, featureTransaction, new TrainingValueValidation(schedule),
                    schedule, new TrainingFoundationQueries(context), promotion),
                new EmployeeEnrolmentService(context, featureAccess, featureEligibility, featureTransaction, clock, promotion))).SeedAsync(cancellationToken);
            // Pas na de afzonderlijk gecommitte moment-/aanvraag-/bufferstappen komt er een bruikbare hint.
            credentials.PublishReady(result);
        }
        catch { credentials.Clear(); throw; }
    }
}
