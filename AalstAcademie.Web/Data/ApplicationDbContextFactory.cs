// Sprint 003: Bouwt voor EF-tooling alleen een context uit een expliciet meegegeven verbinding
// Design-time tooling start Program, reset en demoseed niet automatisch.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace AalstAcademie.Web.Data;

/// <summary>
/// Veilige EF design-time context zonder Program-opstart, JSON/usersecrets of automatische migratie/seed.
/// De aanroeper moet de concrete verbinding expliciet na de EF '--'-grens meegeven.
/// </summary>
public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    /// <summary>Construeert alleen opties en context; opent nooit zelf de databaseverbinding.</summary>
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var configuration = new ConfigurationBuilder().AddCommandLine(args).Build();
        var connection = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("EF-tooling vereist expliciet --ConnectionStrings:DefaultConnection; een standaarddatabase wordt niet gebruikt.");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        return new ApplicationDbContext(options);
    }
}
