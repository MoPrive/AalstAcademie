using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AalstAcademie.Web.Models.Accounts;

/// <summary>De eerste HTML-weergave bevat dezelfde snapshot als latere beveiligde refreshes.</summary>
public sealed record AccountApplicationsIndexViewModel(AccountApplicationsSnapshot Snapshot);

/// <summary>Alleen deze drie velden worden uit een beoordelingsformulier gebonden.</summary>
public sealed class AccountReviewInput
{
    // Dit verborgen veld bewaart de versie waarop de beheerder zijn keuze baseerde.
    // Een nieuwe lijstsnapshot mag deze waarde niet stilzwijgend actualiseren.
    public string ExpectedConcurrencyStamp { get; set; } = string.Empty;
    // Alleen interne aanvragen kunnen bij goedkeuring een gecorrigeerde afdeling krijgen.
    public int? DepartmentId { get; set; }
    // De server maakt de reden verplicht bij weigering, ongeacht browservalidatie.
    public string? RefusalReason { get; set; }
}

/// <summary>Leesgegevens en toegestane reviewinvoer zijn gescheiden; de actor komt nooit uit het formulier.</summary>
public sealed class AccountApplicationDetailsViewModel
{
    // Deze init-properties beschrijven de opgeslagen aanvraag en vormen geen bewerkbaar accountmodel.
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public RequestedAccountType? RequestedAccountType { get; init; }
    public AccountApprovalStatus Status { get; init; }
    public string? Email { get; init; }
    public string? PhoneNumber { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? DepartmentName { get; init; }
    public string? ManagerName { get; init; }
    public string? OrganizationName { get; init; }
    public string? VatNumber { get; init; }
    public DateTime? AccountRequestedAtUtc { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public string? RefusalReason { get; init; }
    // CanReview stuurt de zichtbare bediening; de service controleert dezelfde grenzen opnieuw.
    public bool CanReview { get; init; }
    // Bij HTTP 409 toont de pagina het conflict naast de oorspronkelijke formulierinvoer.
    public bool IsConflict { get; set; }
    public AccountReviewInput Input { get; set; } = new();
    // Een bestaande afdelingslijst voorkomt vrije invoer van namen; de FK wordt ook serverzijdig getoetst.
    public IReadOnlyList<SelectListItem> Departments { get; init; } = [];
}
