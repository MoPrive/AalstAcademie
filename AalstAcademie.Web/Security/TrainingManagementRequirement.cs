// Sprint 003: Benoemt de opleidingspolicy en haar vereiste actuele Lesgever- of Beheerderrol
// De bestaande accountgoedkeuringsgrens blijft aanvullend gelden.
using Microsoft.AspNetCore.Authorization;

namespace AalstAcademie.Web.Security;

/// <summary>De opleidingspolicy vult de bestaande accountgrens aan met actuele featurerollen.</summary>
public static class TrainingPolicies
{
    public const string TrainingManagement = nameof(TrainingManagement);
}

/// <summary>Vereist een huidige Lesgever of Beheerder; een opgeslagen profiel verleent geen rol.</summary>
public sealed record TrainingManagementRequirement : IAuthorizationRequirement;
