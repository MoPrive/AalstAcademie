// Sprint 003: Definieert de zes vaste fictieve demo-identiteiten en hun rollen/statussen
// Het tijdelijke sessiewachtwoord staat niet in deze catalogus.
using AalstAcademie.Web.Models.Identity;

namespace AalstAcademie.Web.Data;

/// <summary>Vaste fictieve accountgegevens, zonder wachtwoorden of herbruikbare stamps.</summary>
public static class DemoAccountCatalog
{
    public const string AdministratorId = "demo-001-administrator";
    public const string InternalInstructorId = "demo-003-internal-instructor";
    public const string ExternalInstructorId = "demo-004-external-instructor";
    public static IReadOnlyList<DemoAccountDescriptor> All { get; } = Array.AsReadOnly<DemoAccountDescriptor>(
    [
        new(AdministratorId, "sprint002.administrator", "beheerder@example.invalid", null, AccountApprovalStatus.Approved, "Demi", "Beheerder", null),
        new("demo-002-employee", "sprint002.employee", "medewerker@example.invalid", RequestedAccountType.Employee, AccountApprovalStatus.Approved, "Noor", "Demo", null),
        new(InternalInstructorId, "sprint002.internal-instructor", "interne-lesgever@example.invalid", RequestedAccountType.InternalInstructor, AccountApprovalStatus.Approved, "Jules", "Demo", null),
        new(ExternalInstructorId, "sprint002.external-instructor", "externe-lesgever@example.invalid", RequestedAccountType.ExternalInstructor, AccountApprovalStatus.Approved, null, null, "Fictief Opleidingsbureau"),
        new("demo-005-pending-employee", "sprint002.pending-employee", "aanvraag-medewerker@example.invalid", RequestedAccountType.Employee, AccountApprovalStatus.Pending, "Robin", "Demo", null),
        new("demo-006-pending-external-instructor", "sprint002.pending-external-instructor", "aanvraag-externe-lesgever@example.invalid", RequestedAccountType.ExternalInstructor, AccountApprovalStatus.Pending, null, null, "Fictief Aanvraagbureau")
    ]);
}

public sealed record DemoAccountDescriptor(string Id, string Key, string Email, RequestedAccountType? Type,
    AccountApprovalStatus Approval, string? FirstName, string? LastName, string? OrganizationName);
