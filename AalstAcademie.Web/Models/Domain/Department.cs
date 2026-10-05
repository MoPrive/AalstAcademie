// Sprint 003: Voegt de omgekeerde navigatie van afdelingen naar geselecteerde opleidingsdoelgroepen toe
// De bestaande afdeling-identiteit en seedafspraken blijven behouden.
namespace AalstAcademie.Web.Models.Domain;

/// <summary>Afdelingsreferentie die via ApplicationUser met een account wordt verbonden.</summary>
public class Department
{
    // SQLite genereert de numerieke primaire sleutel bij het opslaan.
    public int Id { get; set; }
    // De EF-configuratie eist een unieke, niet-lege naam van maximaal 100 tekens.
    public string Name { get; set; } = string.Empty;

    // Sprint 004: één optionele afdelingsverantwoordelijke; dezelfde account mag meerdere afdelingen dragen.
    // De oorspronkelijke nullable FK is ook de concurrencytoken voor de beperkte toewijzingsactie.
    public string? ResponsibleUserId { get; set; }
    public AalstAcademie.Web.Models.Identity.ApplicationUser? ResponsibleUser { get; set; }

    // Doelgroepkoppelingen verschillen per opleiding en staan los van de werkafdeling op ApplicationUser.
    // Een navigatie is geen nieuwe afdelingskolom of globale toelatingsboolean.
    public ICollection<TrainingDepartment> TrainingDepartments { get; set; } = new List<TrainingDepartment>();
}
