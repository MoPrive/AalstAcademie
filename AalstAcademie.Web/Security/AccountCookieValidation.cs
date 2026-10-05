using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using AalstAcademie.Web.Models.Identity;

namespace AalstAcademie.Web.Security;

/// <summary>Behoudt Identity-stampvalidatie en controleert daarna de actuele account- en demogrens.</summary>
public static class AccountCookieValidation
{
    /// <summary>Wordt ingesteld als OnValidatePrincipal van de Identity-applicationcookie.</summary>
    /// <remarks>
    /// Authenticatie beoordeelt of de sessie nog geldig is. De latere accountpolicy bepaalt
    /// of dat geldige account de gevraagde actie mag uitvoeren; Pending is dus geen ongeldige cookie.
    /// </remarks>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        // Identity vergelijkt standaard pas wanneer verstreken tijd > interval.
        // Ook met interval nul kan dezelfde (of een teruggezette) kloktijd de
        // controle overslaan. Controleer daarom eerst altijd de oorspronkelijke
        // cookie: een ingetrokken sessie mag nooit stilzwijgend vernieuwen.
        var signInManager = context.HttpContext.RequestServices
            .GetRequiredService<SignInManager<ApplicationUser>>();
        if (await signInManager.ValidateSecurityStampAsync(context.Principal) is null)
        {
            context.RejectPrincipal();
            context.ShouldRenew = false;
            await signInManager.SignOutAsync();
            return;
        }

        // Behoud vervolgens de standaard Identity-vernieuwing en overige
        // validatie. Program stelt hiervoor het validatie-interval op nul.
        await SecurityStampValidator.ValidatePrincipalAsync(context);
        if (context.Principal?.Identity?.IsAuthenticated != true)
        {
            // Ook bij afwijzing door de standaard validator mag niets opnieuw worden uitgegeven.
            context.ShouldRenew = false;
            return;
        }

        var accounts = context.HttpContext.RequestServices.GetRequiredService<CurrentAccountAccessor>();
        // De requestgebonden lezing wordt daarna ook door de autorisatiehandler gebruikt,
        // zodat dezelfde aanvraag niet met twee verschillende accounttoestanden verdergaat.
        var account = await accounts.GetAsync(context.Principal, context.HttpContext.RequestAborted);
        if (account is not null)
            return;

        // Een geldige stamp kan nog bij een geblokkeerd/verwijderd of buiten demo
        // gekopieerd account horen. Verwerp ook deze cookie zonder vernieuwing.
        context.RejectPrincipal();
        context.ShouldRenew = false;
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
}
