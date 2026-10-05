// Sprint 003: Breidt de bestaande foutvertaling uit zodat beheerrequests juiste HTTP-statussen krijgen en wachtende accounts hun eigen status behouden
// Login en actuele autorisatie blijven aparte stappen.
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace AalstAcademie.Web.Security;

/// <summary>Geeft JSON/hub-aanvragen echte HTTP-fouten en begeleidt wachtende gebruikers naar hun eigen status.</summary>
public sealed class AccountAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler defaultHandler = new();

    /// <summary>Vertaalt geweigerde toegang naar API-statuscodes of passende HTML-navigatie vóór de endpointactie.</summary>
    public async Task HandleAsync(RequestDelegate next, HttpContext context,
        AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Succeeded && IsApiRequest(context.Request))
        {
            // 401 betekent dat een bruikbare aanmelding ontbreekt; 403 betekent dat de huidige rechten tekortschieten.
            // Fetch en SignalR mogen geen login-HTML als schijnbaar succesvolle
            // JSON of transportresponse ontvangen. Geen endpointactie uitvoeren.
            context.Response.StatusCode = authorizeResult.Challenged
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden;
            return;
        }

        if (authorizeResult.Forbidden)
        {
            var account = await context.RequestServices.GetRequiredService<CurrentAccountAccessor>()
                .GetAsync(context.User, context.RequestAborted);
            if (account?.User.AccountApprovalStatus is AccountApprovalStatus.Pending or AccountApprovalStatus.Refused)
            {
                // Deze HTML-navigatie gaat uitsluitend naar de eigen status;
                // de afgewezen beschermde GET/POST wordt niet alsnog uitgevoerd.
                context.Response.Redirect("/Account/Status");
                return;
            }
            // Alleen deze nieuwe beheerschermen geven ook bij gewone HTML-verzoeken een echte403.
            // De oudere account-, snapshot- en hubafhandeling behoudt haar bestaande uitkomsten.
            if (IsTrainingController(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        // Identity verwerkt normale login/AccessDenied-navigatie en succesvolle
        // aanvragen volgens de oorspronkelijke middlewareconventies.
        await defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }

    /// <summary>Endpointmetadata begrenst de HTML403-uitzondering tot de vier concrete opleidingsbeheercontrollers.</summary>
    private static bool IsTrainingController(HttpContext context)
    {
        var controller = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType();
        return controller == typeof(TrainingManagementController) || controller == typeof(CategoriesController)
            || controller == typeof(TrainingMomentsController) || controller == typeof(LocationsController);
    }

    /// <summary>Dezelfde classificatie wordt gebruikt door cookie-redirectevents en autorisatiefouten.</summary>
    public static bool IsApiRequest(HttpRequest request)
    {
        // Snapshot- en hubroutes blijven API's, ook zonder bijzondere headers.
        var path = request.Path.Value ?? string.Empty;
        return request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
            || path.TrimEnd('/').EndsWith("/Snapshot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(request.Headers["X-Requested-With"].ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase)
            || request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase)
            || (request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
