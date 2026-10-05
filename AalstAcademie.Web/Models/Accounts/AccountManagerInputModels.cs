using System.ComponentModel.DataAnnotations;

namespace AalstAcademie.Web.Models.Accounts;

/// <summary>Een eigen profielkeuze bevat geen doelaccount, rollen of SecurityStamp.</summary>
public sealed record AccountManagerInput
{
    public string? ManagerUserId { get; init; }
    [Required(ErrorMessage = "Lees je leidinggevende opnieuw om de profielversie te verkrijgen.")]
    public string? ExpectedConcurrencyStamp { get; init; }
}
