// Sprint 003: Combineert beperkte zaalinput en geautoriseerde schermgegevens voor index, create, edit en deletebevestiging
// Schermflags vervangen geen POST-serviceguards.
namespace AalstAcademie.Web.Models.Training;

/// <summary>Huidige gedeelde zaallabels en referentieguards, zonder deelnemersinformatie.</summary>
public sealed record LocationIndexViewModel(IReadOnlyList<LocationReadModel> Rows);
public sealed class LocationCreateViewModel : LocationCreateInput { }
public sealed class LocationEditViewModel : LocationEditInput
{
    public LocationReadModel Details { get; set; } = null!;
}
public sealed class LocationDeleteViewModel : LocationDeleteInput
{
    public LocationReadModel Details { get; set; } = null!;
}
