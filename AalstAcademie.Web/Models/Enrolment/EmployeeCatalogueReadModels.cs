using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;

namespace AalstAcademie.Web.Models.Enrolment;

/// <summary>Alleen eigen contact-/profielgegevens; de actuele reader geeft geen Identity-entity of rechtenclaims aan de UI.</summary>
public sealed record EmployeeActor(string UserId, int? DepartmentId, RequestedAccountType? AccountType,
    string DisplayName, string? Email, string? PhoneNumber, string? DepartmentName, string? ManagerName);

public sealed record EmployeeCatalogueRow(int MomentId, int TrainingId, string Title, string Category,
    DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, string Location, string? Address,
    string InstructorName, decimal? ExternalTotalPriceEuros, int PhysicalAvailable, int EligibleWaiters,
    int DirectAvailable);

public sealed record EmployeeCataloguePage(TrainingOperationStatus Status, int Page = 1, int TotalPages = 1,
    int Total = 0, int? CategoryId = null, IReadOnlyList<EmployeeCatalogueRow>? Rows = null,
    IReadOnlyList<EmployeeCategoryChoice>? Categories = null);
public sealed record EmployeeCategoryChoice(int Id, string Name);

/// <summary>Originele voorwaarden/momentversies horen bij deze lezing; mislukte POSTs mogen ze niet stil vervangen.</summary>
public sealed record EmployeeMomentReadModel(EmployeeCatalogueRow Row, string? Description,
    bool RequiresMotivation, Guid TrainingVersion, Guid MomentVersion, EmployeeActor Actor,
    bool HasActiveParticipation, bool RefusedThisMoment)
{
    public bool CanRequest => !HasActiveParticipation && !RefusedThisMoment && Row.DirectAvailable > 0;
    public bool CanJoin => !HasActiveParticipation && !RefusedThisMoment && Row.DirectAvailable == 0;
}
public sealed record EmployeeMomentReadResult(TrainingOperationStatus Status, EmployeeMomentReadModel? Moment = null);
