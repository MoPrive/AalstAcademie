// Sprint 003: Voert migraties en vaste rollen/afdelingen uit na de legacyvoorcontrole
// Buiten de demomodus blijven bestaande accounts en opgeslagen gebruikerskeuzes behouden.
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Data;

/// <summary>
/// Zorgt bij opstarten voor het juiste schema en vaste referentiegegevens.
/// Maakt geen gebruikers, wachtwoorden of gebruikersroltoekenningen aan.
/// </summary>
public sealed class DatabaseInitializer(ApplicationDbContext context, RoleManager<IdentityRole> roleManager)
{
    /// <summary>Past ontbrekende migraties toe en voegt alleen ontbrekende rollen/afdelingen toe.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Controleer fysieke legacyshape vóór EF nieuwe velden leest of schema/referenties schrijft.
        await AccountManagerMigrationPreflight.ValidateAsync(context, cancellationToken);
        await LegacyTrainingMigrationPreflight.ValidateAsync(context, cancellationToken);
        // Migrate gebruikt de migratiegeschiedenis; behoud van oude accounts is voor
        // deze foundationmigratie met een upgrade-test bewezen.
        // Bij een fout stopt de opstart in plaats van met een verkeerd schema door te gaan.
        await context.Database.MigrateAsync(cancellationToken);
        // RoleManager gebruikt de standaard Identity-tabellen, inclusief normalisatie.
        // Bestaancontrole maakt opeenvolgende initialisaties idempotent.
        foreach (var name in new[] { RoleNames.Medewerker, RoleNames.Lesgever, RoleNames.Beheerder })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await roleManager.RoleExistsAsync(name)) continue;
            // Identity kan een foutresultaat teruggeven zonder exception: controleer dit expliciet.
            var result = await roleManager.CreateAsync(new IdentityRole(name));
            if (!result.Succeeded)
                throw new InvalidOperationException($"Rol '{name}' kon niet worden aangemaakt: " +
                    string.Join("; ", result.Errors.Select(x => $"{x.Code}: {x.Description}")));
        }
        // Fictieve referentiedata voor het lokale project; bestaande gelijknamige afdelingen
        // worden niet hernoemd/vervangen en extra afdelingen blijven behouden.
        foreach (var name in new[] { "ICT", "Personeel en Organisatie", "Onderhoud", "Administratie" })
        {
            if (!await context.Departments.AnyAsync(x => x.Name == name, cancellationToken))
                context.Departments.Add(new Department { Name = name });
        }
        // Schrijf de eventueel toegevoegde afdelingen naar de gekozen database.
        await context.SaveChangesAsync(cancellationToken);
    }
}
