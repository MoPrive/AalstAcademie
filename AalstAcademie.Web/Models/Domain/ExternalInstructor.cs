using AalstAcademie.Web.Models.Identity;

namespace AalstAcademie.Web.Models.Domain;

/// <summary>Externe lesgever met organisatienaam en optioneel btw-nummer.</summary>
public class ExternalInstructor
{
    // Eigen profielsleutel voor toekomstige opleidingsrelaties.
    public int Id { get; set; }
    // Verplichte unieke FK: maximaal één extern profiel per Identity-account.
    public string ApplicationUserId { get; set; } = string.Empty;
    // Identity bewaart de login/contactgegevens; null! onderdrukt alleen de compilerwaarschuwing.
    public ApplicationUser User { get; set; } = null!;
    // De organisatie is de lesgeversnaam; er is geen aparte contactpersonenadministratie.
    public string OrganizationName { get; set; } = string.Empty;
    // Optioneel gegeven, beperkt tot 32 tekens door regels en databaseconfiguratie.
    public string? VatNumber { get; set; }
}
