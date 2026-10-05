namespace AalstAcademie.Web.Models.Accounts;

public sealed record DepartmentResponsibleRow(int Id, string Name, string? ResponsibleName,
    DepartmentResponsibleInput Input);
public sealed class DepartmentResponsiblesViewModel
{
    public IReadOnlyList<DepartmentResponsibleRow> Departments { get; init; } = [];
    public IReadOnlyList<AccountManagerChoice> Candidates { get; init; } = [];
    // Alleen het foutformulier ontvangt oude invoer; overige rijen worden nooit meegeschreven.
    public int? FailedDepartmentId { get; set; }
    public DepartmentResponsibleInput? FailedInput { get; set; }
}
