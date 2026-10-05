using System.Security.Claims;
using AalstAcademie.Web.Controllers;
using AalstAcademie.Web.Services.Enrolment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc.Controllers;
namespace AalstAcademie.Web.Security;

/// <summary>De opgeslagen status, rol en afdeling beslissen. De schrijfdienst herhaalt dit onder haar write-lock.</summary>
public sealed class EmployeeParticipationHandler(EmployeeAccessReader access) : AuthorizationHandler<EmployeeParticipationRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, EmployeeParticipationRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        var ct = context.Resource is HttpContext http ? http.RequestAborted : CancellationToken.None;
        if (await access.ReadEmployeeAsync(context.User.FindFirstValue(ClaimTypes.NameIdentifier), ct) is not null)
            context.Succeed(requirement);
    }
}

/// <summary>Begrensde HTML403 voor uitsluitend de vijf nieuwe controllers; bestaande account/API-statusafhandeling blijft leidend.</summary>
public sealed class EmployeeAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AccountAuthorizationResultHandler existing = new();
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        var controller = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType();
        var feature = controller == typeof(EmployeeCatalogueController) || controller == typeof(EmployeeEnrolmentsController)
            || controller == typeof(MyRegistrationsController) || controller == typeof(AccountManagerController)
            || controller == typeof(DepartmentResponsiblesController);
        if (result.Forbidden && feature && !AccountAuthorizationResultHandler.IsApiRequest(context.Request))
        {
            var account = await context.RequestServices.GetRequiredService<CurrentAccountAccessor>().GetAsync(context.User, context.RequestAborted);
            if (account?.IsApproved == true)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        await existing.HandleAsync(next, context, policy, result);
    }
}
