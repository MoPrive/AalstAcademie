// Sprint 003: Geeft getypeerde succes-, validatie-, forbidden-, not-found-, conflict- en opslaguitkomsten
// Controllers vertalen die naar formulieren, PRG en passende HTTP-statussen.
namespace AalstAcademie.Web.Services.Training;

/// <summary>De service geeft businessuitkomsten; de latere HTTP-laag kiest statuscode en formulier.</summary>
public enum TrainingOperationStatus
{
    Success,
    Invalid,
    NotFound,
    Forbidden,
    Conflict,
    StorageUnavailable
}

/// <summary>
/// Bevat een veilige uitkomst en veldfeedback, zonder DB-details of vervangende actuele versietoken.
/// Een conflict geeft dus geen automatische toestemming om een gelezen versie te vernieuwen.
/// </summary>
public sealed record TrainingOperationResult(TrainingOperationStatus Status, int? Id = null,
    IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public bool Succeeded => Status == TrainingOperationStatus.Success;
}
