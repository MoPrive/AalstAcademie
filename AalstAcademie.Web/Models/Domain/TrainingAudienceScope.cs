// Sprint 003: Onderscheidt Unassigned, AllDepartments en SelectedDepartments
// Een nieuwe definitie geeft niet stilzwijgend toegang aan alle afdelingen.
namespace AalstAcademie.Web.Models.Domain;

/// <summary>Expliciete doelgroepkeuze, onafhankelijk van opleidingsarchief en accountgoedkeuring.</summary>
public enum TrainingAudienceScope
{
    // Veilige beginwaarde: nog geen doelgroep, dus ook geen impliciete toelating voor alle afdelingen.
    Unassigned = 0,
    // Omvat later ook nieuw toegevoegde afdelingen, zonder individuele koppelingen te bewaren.
    AllDepartments = 1,
    // Vereist één of meer unieke bestaande afdelingen; alle huidige aanvinken blijft deze scope.
    SelectedDepartments = 2
}
