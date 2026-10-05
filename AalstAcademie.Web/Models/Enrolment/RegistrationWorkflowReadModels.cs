using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;

namespace AalstAcademie.Web.Models.Enrolment;

/// <summary>Actuele contactgegevens uitsluitend binnen huidige eigenaar/adminscope; geen Identity-geheimen.</summary>
public sealed record RegistrationManagementRow(int Id, int MomentId, int TrainingId, string Title,
    string ParticipantName, string? Email, string? Phone, string? Department, string? Manager,
    DateOnly Date, TimeOnly StartTime, RegistrationStatus Status, string? Motivation,
    string? DecisionReason, DateTime RequestedAtUtc, Guid RegistrationVersion, Guid MomentVersion,
    bool CanConfirm, bool CanRefuse);
public sealed record RegistrationManagementBufferRow(int Id, int TrainingId, string Title,
    string ParticipantName, string? Email, string? Phone, string? Department, string? Manager,
    string? Motivation, DateTime JoinedAtUtc);
public sealed record RegistrationManagementReadModel(TrainingOperationStatus Status,
    IReadOnlyList<RegistrationManagementRow> Registrations, IReadOnlyList<RegistrationManagementBufferRow> Waitlist,
    int RegistrationPage, int RegistrationPages, int WaitlistPage, int WaitlistPages,
    int? TrainingId = null, int? MomentId = null, RegistrationStatus? FilterStatus = null);
public sealed record RegistrationManagementDetailResult(TrainingOperationStatus Status, RegistrationManagementRow? Registration = null);
public sealed record OwnRegistrationCancelReadModel(int Id, int MomentId, int TrainingId, string Title,
    DateOnly Date, TimeOnly StartTime, RegistrationStatus Status, string? Motivation, string? DecisionReason,
    Guid RegistrationVersion, Guid MomentVersion, bool CanCancel);
public sealed record OwnRegistrationCancelReadResult(TrainingOperationStatus Status, OwnRegistrationCancelReadModel? Registration = null);
public sealed record TrainingMomentCancelReadModel(int Id, int TrainingId, string Title, string Location,
    DateOnly Date, TimeOnly StartTime, int RequestedCount, int ConfirmedCount, Guid MomentVersion, bool CanCancel);
