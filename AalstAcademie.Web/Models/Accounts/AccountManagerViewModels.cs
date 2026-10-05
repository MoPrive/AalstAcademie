namespace AalstAcademie.Web.Models.Accounts;

/// <summary>Dropdown met alleen gebruikerssleutel/persoonlijke naam; geen contactgegevens, rollen of stamps.</summary>
public sealed record AccountManagerChoice(string? UserId, string DisplayName);
public sealed class AccountManagerViewModel
{
    public string DisplayName { get; init; } = string.Empty;
    public string? CurrentManagerName { get; init; }
    public AccountManagerInput Input { get; set; } = new();
    public IReadOnlyList<AccountManagerChoice> Choices { get; init; } = [];
}
