using AalstAcademie.Web.Models.Identity;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Alleen aanvraaggegevens zijn invoer; rollen en systeemmetadata komen van de server.</summary>
public sealed record AccountRegistrationCommand
{
    // De nullable enum moet eerst een van de drie publieke types blijken; er bestaat geen publieke beheerdersoptie.
    public RequestedAccountType? RequestedAccountType { get; init; }
    public string? Email { get; init; }
    public string? PhoneNumber { get; init; }
    // Wachtwoord en bevestiging zijn tijdelijke requestinvoer. Identity bewaart alleen de wachtwoordhash.
    public string? Password { get; init; }
    public string? ConfirmPassword { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public int? DepartmentId { get; init; }
    // Sprint 004: optionele bestaande verantwoordelijke; vrije tekst is geen geldige accountreferentie.
    public string? ManagerUserId { get; init; }
    public string? OrganizationName { get; init; }
    public string? VatNumber { get; init; }
}

/// <summary>De drie toegestane statusovergangen; geen algemene accountbewerking.</summary>
public enum AccountReviewAction { Approve, Refuse, Reopen }

/// <summary>De versie komt uit de reviewpagina; de actor wordt afzonderlijk serverzijdig geleverd.</summary>
/// <remarks>
/// Dit kleine commando voorkomt overposting: de beheerder kan geen accounttype,
/// rollen, beoordelaar of tijdstip meesturen als vrij te wijzigen accountvelden.
/// </remarks>
public sealed record AccountReviewCommand(string TargetUserId, string ExpectedConcurrencyStamp,
    AccountReviewAction Action, int? DepartmentId = null, string? RefusalReason = null);
