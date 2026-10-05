using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;

namespace AalstAcademie.Web.Models.Enrolment;

public sealed record OwnRegistrationRow(int Id, int MomentId, int TrainingId, string Title, string Category,
    string InstructorName, string Location, string? Address, DateOnly Date, TimeOnly StartTime,
    TimeOnly EndTime, decimal? ExternalTotalPriceEuros, RegistrationStatus Status, string? Motivation,
    string? DecisionReason, DateTime RequestedAtUtc)
{
    public bool CanCancel { get; init; }
}

public sealed record OwnWaitlistRow(int Id, int TrainingId, string Title, string Category,
    string InstructorName, decimal? ExternalTotalPriceEuros, string? Motivation, DateTime JoinedAtUtc,
    DateTime? ClosedAtUtc, Guid Version);

/// <summary>Iedere lijst heeft eigen pagina20; eigen historie blijft leesbaar na alleen doelgroep-/rolverlies.</summary>
public sealed record OwnRegistrationsReadModel(TrainingOperationStatus Status, IReadOnlyList<OwnRegistrationRow> Registrations,
    IReadOnlyList<OwnWaitlistRow> Waitlist, int RegistrationPage, int RegistrationPages,
    int WaitlistPage, int WaitlistPages);
public sealed record OwnWithdrawReadResult(TrainingOperationStatus Status, OwnWaitlistRow? Entry = null);
