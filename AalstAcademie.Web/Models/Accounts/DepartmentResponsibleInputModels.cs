namespace AalstAcademie.Web.Models.Accounts;

/// <summary>Ook null is een oorspronkelijke FK-waarde; de controller controleert dat de formulierkey echt aanwezig is.</summary>
public sealed record DepartmentResponsibleInput
{
    public string? ResponsibleUserId { get; init; }
    public string? ExpectedResponsibleUserId { get; init; }
}
