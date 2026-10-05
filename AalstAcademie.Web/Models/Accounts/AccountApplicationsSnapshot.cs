namespace AalstAcademie.Web.Models.Accounts;

/// <summary>Beperkte, actuele lijstinformatie; de reviewversie geeft nooit aanmeldrechten.</summary>
/// <remarks>
/// Teller, filter, pagina en rijen vormen één serverantwoord. De browser vervangt alleen
/// de leesweergave met deze gegevens; een reeds geopend beoordelingsformulier behoudt zijn invoer.
/// </remarks>
public sealed record AccountApplicationsSnapshot(int TotalPending, string Status, int Page, int TotalPages,
    IReadOnlyList<AccountApplicationRow> Rows);

/// <summary>Alleen de velden die de beheerder voor een aanvraagrij nodig heeft verlaten de server.</summary>
/// <remarks>
/// ConcurrencyStamp identificeert de gelezen reviewversie. SecurityStamp en wachtwoordhash
/// ontbreken: die zijn voor sessiebeveiliging en horen niet in een JSON-lijst.
/// </remarks>
public sealed record AccountApplicationRow(string Id, string DisplayName, string? RequestedAccountType,
    string? DepartmentOrOrganization, DateTime? AccountRequestedAtUtc, string Status, string? ConcurrencyStamp);
