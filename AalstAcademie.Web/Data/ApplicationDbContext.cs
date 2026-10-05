// Sprint 003: Verbindt Identity, profielen en de zeven opleidingstabellen in één EF-context
// De context registreert mappings; services bepalen wie er op welk moment mag schrijven.
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Models.Domain;

namespace AalstAcademie.Web.Data;

/// <summary>
/// Eén EF Core-context voor de bestaande Identity-tabellen en de accountdomeintabellen.
/// Het generieke argument laat Identity voortaan onze ApplicationUser opslaan.
/// </summary>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    // DbSet is het EF-ingangspunt om deze entiteiten te bevragen/toevoegen/verwijderen.
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<InternalInstructor> InternalInstructors => Set<InternalInstructor>();
    public DbSet<ExternalInstructor> ExternalInstructors => Set<ExternalInstructor>();
    // Sprint 003 gebruikt dezelfde context/transactie voor opleiding, categorie en volledige linkset.
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Training> Trainings => Set<Training>();
    public DbSet<TrainingDepartment> TrainingDepartments => Set<TrainingDepartment>();
    // Planning/zaal en echte historie delen dezelfde context en schrijftransactie.
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<TrainingMoment> TrainingMoments => Set<TrainingMoment>();
    public DbSet<Registration> Registrations => Set<Registration>();
    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();

    /// <summary>Bouwt eerst het standaard Identity-model en voegt daarna eigen opslagregels toe.</summary>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Niet weglaten: deze aanroep legt Identity-sleutels, relaties en tabelnamen vast.
        base.OnModelCreating(builder);
        AccountFoundationConfiguration.Configure(builder);
        TrainingConfiguration.Configure(builder);
    }
}
