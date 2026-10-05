// Sprint 003: Definieert beperkte serviceopdrachten voor metadata, doelgroep, zaal en moment
// ExpectedVersion en veldpresence zijn expliciet; eigenaaroverdracht en statuswijziging zijn geen gewone commands.
using AalstAcademie.Web.Models.Domain;

namespace AalstAcademie.Web.Services.Training;

/// <summary>De herbruikbare definitie heeft metadata en voorwaarden; planning hoort bij een apart moment.</summary>
public sealed record TrainingFields(string? Title, string? Description, int? CategoryId,
    decimal? ExternalTotalPriceEuros, bool RequiresMotivation);

/// <summary>Alleen een beheerder selecteert een eigenaar; iedere nieuwe definitie begint zonder doelgroep.</summary>
public sealed record TrainingCreateCommand(TrainingFields Fields, string? OwnerUserId = null);

/// <summary>
/// De vertrouwde HTTP-laag bepaalt veld-aanwezigheid. Deze flags worden nooit aan een formulier gebonden;
/// weggelaten voorwaarden behouden hun opgeslagen waarde in plaats van null of false te worden.
/// </summary>
public sealed record TrainingUpdateCommand(int Id, Guid? ExpectedVersion, TrainingFields Fields,
    bool PriceSupplied = true, bool MotivationSupplied = true);

/// <summary>Doelgroep en volledige afdelingsset worden als afzonderlijke beheerdersactie atomair vervangen.</summary>
public sealed record TrainingAudienceCommand(int Id, Guid? ExpectedVersion, TrainingAudienceScope Scope,
    IReadOnlyList<int> DepartmentIds);

/// <summary>Naam en optioneel adres identificeren een zaal; een positief maximum is expliciete invoer.</summary>
public sealed record LocationFields(string? Name, string? Address, int? MaximumCapacity);
public sealed record LocationCreateCommand(LocationFields Fields);
public sealed record LocationUpdateCommand(int Id, Guid? ExpectedVersion, LocationFields Fields);
public sealed record LocationDeleteCommand(int Id, Guid? ExpectedVersion);

/// <summary>Een nieuw moment krijgt eigen planning, zaal en maximum, zonder eigenaar- of statusinvoer.</summary>
public sealed record TrainingMomentFields(DateOnly? Date, TimeOnly? StartTime, TimeOnly? EndTime,
    int? LocationId, int? MaximumParticipants);
public sealed record TrainingMomentCreateCommand(int TrainingId, TrainingMomentFields Fields);

/// <summary>Planningedit kan het deelnemersmaximum niet ongemerkt wijzigen.</summary>
public sealed record TrainingMomentPlanningFields(DateOnly? Date, TimeOnly? StartTime, TimeOnly? EndTime, int? LocationId);
public sealed record TrainingMomentUpdateCommand(int Id, Guid? ExpectedVersion, TrainingMomentPlanningFields Fields);

/// <summary>Maximumedit laat datum, uren, zaal, eigenaar en doelgroep ongemoeid.</summary>
public sealed record TrainingMomentCapacityCommand(int Id, Guid? ExpectedVersion, int? MaximumParticipants);

/// <summary>Categorieaanmaak en hernoeming behouden hun bestaande naam/shadowversiecontract.</summary>
public sealed record CategoryCreateCommand(string? Name);
public sealed record CategoryRenameCommand(int Id, Guid? ExpectedVersion, string? Name);
