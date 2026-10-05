using Microsoft.AspNetCore.Authorization;
namespace AalstAcademie.Web.Security;

/// <summary>Nieuwe deelname vereist actuele medewerkersrechten; eigen historie gebruikt de bestaande ApprovedAccount-policy.</summary>
public static class EmployeePolicies
{
    public const string EmployeeParticipation = nameof(EmployeeParticipation);
}
public sealed record EmployeeParticipationRequirement : IAuthorizationRequirement;
