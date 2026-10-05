namespace AalstAcademie.Web.Security;

/// <summary>Voorkomt dat nog niet ondersteunde packaged Identity-pagina's via directe GET/POST worden uitgevoerd.</summary>
public sealed class IdentityEndpointAvailabilityMiddleware(RequestDelegate next)
{
    // Alleen deze pagina's behoren tot de lokale accountworkflow. Een verborgen
    // link is onvoldoende: reset-, mail-, provider- en Manage-routes moeten ook
    // serverzijdig buiten bereik van sender/token/profielwijzigingslogica blijven.
    private static readonly HashSet<string> AllowedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Identity/Account/Login",
        "/Identity/Account/Register",
        "/Identity/Account/Logout",
        "/Identity/Account/RegisterConfirmation",
        "/Identity/Account/AccessDenied",
        "/Identity/Account/Lockout"
    };

    /// <summary>Past dezelfde route-whitelist op GET en POST toe, vóór packaged Identity-code wordt bereikt.</summary>
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/Identity", StringComparison.OrdinalIgnoreCase)
            && !AllowedPaths.Contains((context.Request.Path.Value ?? string.Empty).TrimEnd('/')))
        {
            // Controleer de volledige route, onafhankelijk van HTTP-methode.
            // Manage/subpagina's en callbacks kunnen niet door suffixen passeren.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        // Gewone MVC-routes en expliciet ondersteunde Identity-pagina's gaan verder in de pipeline.
        return next(context);
    }
}
