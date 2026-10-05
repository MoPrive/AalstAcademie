using AalstAcademie.Web.Models.Identity;

namespace AalstAcademie.Web.Models.Domain;

/// <summary>Interne lesgeveridentiteit; persoonlijke naam en afdeling worden via User gelezen.</summary>
public class InternalInstructor
{
    // Profielsleutel voor toekomstige verwijzingen vanuit opleidingen.
    public int Id { get; set; }
    // Verplichte unieke FK: maximaal één intern profiel per Identity-account.
    public string ApplicationUserId { get; set; } = string.Empty;
    // EF vult deze navigatie bij het laden met Include; null! is geen runtimevalidatie.
    public ApplicationUser User { get; set; } = null!;
}
