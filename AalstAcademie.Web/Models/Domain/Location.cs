// Sprint 003: Bewaart gedeelde zaalnaam, optioneel adres, positief zaalmaximum en versie
// Alle momenten verwijzen naar dezelfde huidige zaalgegevens.
namespace AalstAcademie.Web.Models.Domain;

/// <summary>Gedeelde zaalgegevens; capaciteit en naam worden ook bij historische momenten actueel gelezen.</summary>
public class Location
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public int MaximumCapacity { get; set; }
    // De applicatie vergelijkt en roteert deze versie; er is geen database-rowversion.
    public Guid Version { get; set; } = Guid.NewGuid();
    // Iedere referentie, ook verleden of Cancelled, verhindert fysiek zaalverwijderen.
    public ICollection<TrainingMoment> Moments { get; set; } = new List<TrainingMoment>();
}
