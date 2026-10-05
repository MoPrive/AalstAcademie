using Microsoft.AspNetCore.Authorization;

namespace AalstAcademie.Web.Security;

/// <summary>Vaste policynamen voor eigen status, toepassing en beoordeling door een beheerder.</summary>
public static class AccountPolicies
{
    // Statuslezing vereist een bestaand, niet-geblokkeerd account; Pending/Refused zijn daarvoor toegestaan.
    public const string AuthenticatedAccount = nameof(AuthenticatedAccount);
    // Toepassingspagina's eisen daarnaast administratieve goedkeuring.
    public const string ApprovedAccount = nameof(ApprovedAccount);
    // Aanvraagbeheer en de hub eisen goedkeuring én de actuele beheerdersrol.
    public const string ApprovedAdministrator = nameof(ApprovedAdministrator);
}

/// <summary>Beschrijft de benodigde actuele rechten; de handler vraagt deze uit de database op.</summary>
/// <remarks>Dit record is een policyregel, geen accountgegeven en geen rolclaim die in een cookie wordt bewaard.</remarks>
public sealed record AccountAccessRequirement(
    bool RequireApproval = false,
    bool RequireAdministrator = false) : IAuthorizationRequirement;
