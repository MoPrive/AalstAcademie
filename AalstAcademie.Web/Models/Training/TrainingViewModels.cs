// Sprint 003: Combineert geautoriseerde opleidingsreads en beperkte formulierinput
// Historische voorwaarden worden leesbaar getoond zonder verborgen null/false-defaults te posten.
namespace AalstAcademie.Web.Models.Training;

/// <summary>De geautoriseerde pagina en referenties staan los van formulierbinding.</summary>
public sealed record TrainingIndexViewModel(TrainingPageReadModel Page, TrainingListFilter Filter,
    TrainingReferencesReadModel References, bool IsAdministrator);

/// <summary>Details blijven gewone geprojecteerde tekst; de huidige actor bepaalt de getoonde acties.</summary>
public sealed record TrainingDetailsViewModel(TrainingDetailsReadModel Details, bool IsAdministrator);

/// <summary>Flat inputnamen laten Razor dezelfde oorspronkelijke ModelState-waarden opnieuw tonen.</summary>
public sealed class TrainingCreateViewModel : TrainingCreateInput
{
    public bool IsAdministrator { get; init; }
    public TrainingOwnerReadModel? Owner { get; init; }
    public IReadOnlyList<TrainingOwnerReadModel> Owners { get; init; } = [];
    public TrainingReferencesReadModel References { get; init; } = new([], []);
    public bool IsConflict { get; set; }
    public string? Message { get; set; }
}

/// <summary>De huidige details geven uitleg; ExpectedVersion blijft altijd de oorspronkelijke formulierwaarde.</summary>
public sealed class TrainingEditViewModel : TrainingEditInput
{
    public int Id => Details.Training.Id;
    public TrainingDetailsReadModel Details { get; init; } = null!;
    public TrainingReferencesReadModel References { get; init; } = new([], []);
    public bool IsConflict { get; set; }
    public string? Message { get; set; }
}

/// <summary>De admin ziet actuele afdelingen naast zijn behouden scope/linkinvoer, zonder automatische deduplicatie.</summary>
public sealed class TrainingAudienceViewModel : TrainingAudienceInput
{
    public int Id => Details.Training.Id;
    public TrainingDetailsReadModel Details { get; init; } = null!;
    public TrainingReferencesReadModel References { get; init; } = new([], []);
    public bool IsConflict { get; set; }
    public string? Message { get; set; }
}
