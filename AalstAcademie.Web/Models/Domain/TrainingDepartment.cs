// Sprint 003: Legt de veel-op-veel-koppeling vast tussen één opleiding en geselecteerde afdelingen met een samengestelde sleutel
// AllDepartments gebruikt geen volledige kopie van alle links.
namespace AalstAcademie.Web.Models.Domain;

/// <summary>Een geselecteerde doelgroepafdeling; alleen de combinatie van beide FKs is de sleutel.</summary>
public class TrainingDepartment
{
    public int TrainingId { get; set; }
    public Training Training { get; set; } = null!;
    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
}
