// Sprint 003: Definieert filters, paginaresultaten, owner/categoryreferenties en huidige opleidingdetails
// Het readcontract bevat geen oude archive- of planningvelden op de definitie.
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;

namespace AalstAcademie.Web.Models.Training;

/// <summary>Getypeerde leesfilters; onbekende geposte waarden moeten later expliciet worden afgewezen.</summary>
public enum TrainingAudienceFilter { All, Unassigned, AllDepartments, SelectedDepartments }

/// <summary>Definities hebben geen archiefstatus; actuele scope en paginaomvang twintig komen uit de query.</summary>
public sealed record TrainingListFilter(TrainingAudienceFilter Audience = TrainingAudienceFilter.All,
    int? CategoryId = null, int Page = 1);

/// <summary>Beperkte referentiegegevens; Category.Version komt uit de technische shadowproperty.</summary>
public sealed record TrainingCategoryReadModel(int Id, string Name, Guid Version);
public sealed record TrainingDepartmentReadModel(int Id, string Name);

/// <summary>UserId is de unieke eigenaarselectie; ProfileId alleen leesinformatie, geen vrije formulier-FK.</summary>
public sealed record TrainingOwnerReadModel(string UserId, bool IsExternal, int ProfileId, string DisplayName);

/// <summary>Actuele categorie-/afdelingkeuzes, zonder automatische voorbeelden of accountgegevens.</summary>
public sealed record TrainingReferencesReadModel(IReadOnlyList<TrainingCategoryReadModel> Categories,
    IReadOnlyList<TrainingDepartmentReadModel> Departments);

/// <summary>Gedeelde definitie met huidige categorie/eigenaar; datum, zaal en maximum horen bij momenten.</summary>
public sealed record TrainingRowReadModel(int Id, string Title, int CategoryId, string CategoryName,
    string OwnerUserId,
    string OwnerDisplayName, bool IsExternalOwner, decimal? ExternalTotalPriceEuros,
    TrainingAudienceScope AudienceScope, bool RequiresMotivation, Guid Version);

/// <summary>Beschrijving blijft gewone tekst; actieflags zijn leesfeedback, geen vervanging voor mutatieautorisatie.</summary>
public sealed record TrainingDetailsReadModel(TrainingRowReadModel Training, string? Description,
    IReadOnlyList<TrainingDepartmentReadModel> Departments, bool CanEdit, bool CanAssignAudience,
    bool CanChangeConditions, bool HasHistory, IReadOnlyList<TrainingMomentRowReadModel> Moments, bool CanCreateMoment);

/// <summary>Een begrensde scoped pagina met duidelijke empty/out-of-range- en ontbrekende-categorieinformatie.</summary>
public sealed record TrainingPageReadModel(IReadOnlyList<TrainingRowReadModel> Rows, int Page,
    int TotalPages, int TotalCount, bool HasCategories);

/// <summary>Ook directe querycalls kunnen falen; alleen een succesvolle geautoriseerde query levert Value.</summary>
public sealed record TrainingQueryResult<T>(TrainingOperationStatus Status, T? Value = null,
    IReadOnlyDictionary<string, string[]>? Errors = null) where T : class
{
    public bool Succeeded => Status == TrainingOperationStatus.Success;
}
