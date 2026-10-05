// Sprint 003: Projecteert huidige zaalgegevens, versie en echte referentie/minimumflags zonder deelnemersinformatie
// Het beschermde minimum volgt geplande momentmaxima, niet bezette plaatsen.
namespace AalstAcademie.Web.Models.Training;

/// <summary>Actuele gedeelde zaalgegevens; minimum is gepland maximum vóór einde, geen deelnemersbezetting.</summary>
public sealed record LocationReadModel(int Id, string Name, string? Address, int MaximumCapacity,
    Guid Version, bool HasMoments, int MinimumCapacity)
{
    // Ook een afgelopen of geannuleerd moment verhindert fysieke verwijdering van zijn zaal.
    public bool CanDelete => !HasMoments;
}
