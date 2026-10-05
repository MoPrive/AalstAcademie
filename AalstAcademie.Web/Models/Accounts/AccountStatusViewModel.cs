using AalstAcademie.Web.Models.Identity;

namespace AalstAcademie.Web.Models.Accounts;

/// <summary>Alleen de eigen beoordeling is zichtbaar; er worden geen gegevens van andere accounts opgezocht.</summary>
/// <remarks>
/// Pending en Refused krijgen hiermee uitleg na aanmelding. Een status lezen verleent
/// geen toegang tot de toepassing; daarvoor geldt afzonderlijk de goedkeuringspolicy.
/// </remarks>
public sealed record AccountStatusViewModel(AccountApprovalStatus Status, DateTimeOffset? ReviewedAt,
    string? RefusalReason, bool IsAdministrator);
