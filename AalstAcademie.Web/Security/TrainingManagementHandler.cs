// Sprint 003: Vraagt via TrainingAccessReader de huidige opgeslagen account- en roltoestand op
// De policy verleent geen rechten op basis van een oude cookieclaim.
using System.Security.Claims;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Authorization;

namespace AalstAcademie.Web.Security;

/// <summary>Vraagt huidige accountstatus en DB-rollen op; oude cookie-rolclaims zijn geen bewijs.</summary>
public sealed class TrainingManagementHandler(TrainingAccessReader access)
    : AuthorizationHandler<TrainingManagementRequirement>
{
    /// <summary>Beslist alleen over toegang; de mutatieservice herhaalt deze controle binnen haar transactie.</summary>
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,
        TrainingManagementRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        var token = context.Resource is HttpContext http ? http.RequestAborted : CancellationToken.None;
        var actor = await access.ReadActorAsync(context.User.FindFirstValue(ClaimTypes.NameIdentifier), token);
        if (actor is not null) context.Succeed(requirement);
    }
}
