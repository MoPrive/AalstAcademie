using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Voegt de account- en demogrens toe aan de normale Identity-aanmeldvoorwaarden.</summary>
public sealed class ApplicationSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    DemoMode demoMode)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory,
        optionsAccessor, logger, schemes, confirmation)
{
    /// <summary>Een blokkering of demoaccount buiten demomode kan nooit via een geldig wachtwoord binnen.</summary>
    /// <remarks>
    /// Het wachtwoord bewijst de identiteit; het verleent nog geen toepassingsrechten.
    /// Buiten de demo blijven de normale Identity-eisen, zoals accountbevestiging, gelden.
    /// </remarks>
    public override Task<bool> CanSignInAsync(ApplicationUser user)
    {
        // Pending/Refused mogen zich aanmelden voor hun eigen status; goedkeuring
        // hoort bij autorisatie. De basis behoudt bevestigings- en Identity-regels.
        if (user.IsBlocked || (!demoMode.IsEnabled && user.DemoSeedKey is not null))
            return Task.FromResult(false);
        return base.CanSignInAsync(user);
    }
}
