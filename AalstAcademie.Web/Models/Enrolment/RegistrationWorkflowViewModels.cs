namespace AalstAcademie.Web.Models.Enrolment;

/// <summary>Bewaar POST-invoer en oorspronkelijke tokens; een fout geeft geen stil vernieuwde toestemming.</summary>
public sealed class RegistrationReviewViewModel
{
    public RegistrationManagementRow Registration { get; init; } = null!;
    public RegistrationRefuseInput Input { get; init; } = new();
    public bool IsConflict { get; init; }
}
public sealed class RegistrationCancelViewModel
{
    public OwnRegistrationCancelReadModel Registration { get; init; } = null!;
    public RegistrationCancelInput Input { get; init; } = new();
    public bool IsConflict { get; init; }
}
public sealed class TrainingMomentCancelViewModel
{
    public TrainingMomentCancelReadModel Moment { get; init; } = null!;
    public TrainingMomentCancelInput Input { get; init; } = new();
    public bool IsConflict { get; init; }
}
