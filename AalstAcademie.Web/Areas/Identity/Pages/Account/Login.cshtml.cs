// Sprint 003: Vult de bestaande Identity-login aan met de gereedstaande tijdelijke demohint
// De hint is ongecachet; gewone wachtwoord-, lockout- en lokale ReturnUrl-controles blijven gelden.
using System.ComponentModel.DataAnnotations;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AalstAcademie.Web.Areas.Identity.Pages.Account;

/// <summary>Echte Identity-wachtwoordaanmelding met lockout en veilige lokale vervolgroute.</summary>
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> users,
    DemoMode demoMode, DemoCredentials credentials) : PageModel
{
    // Alleen deze beperkte invoer wordt uit het formulier gebonden; Identity beheert de werkelijke gebruiker.
    [BindProperty]
    public LoginInput Input { get; set; } = new();
    public string? ReturnUrl { get; private set; }
    // Alleen effectieve demo én volledig opgebouwde sessie geeft een hint; deze getter genereert niets.
    public DemoCredentialHint? DemoHint => demoMode.IsEnabled && credentials.TryGetReadyHint(out var hint) ? hint : null;

    /// <summary>Onthoudt uitsluitend een veilige lokale vervolgroute voor het loginformulier.</summary>
    public void OnGet(string? returnUrl = null) => ReturnUrl = LocalReturnUrl(returnUrl);

    /// <summary>Verifieert de invoer met Identity en stuurt nog niet goedgekeurde accounts naar hun eigen status.</summary>
    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = LocalReturnUrl(returnUrl);
        if (!ModelState.IsValid) return FailedPage();
        var email = Input.Email.Trim();
        // Identity bewaakt de wachtwoordhash, toegestane aanmelding en mislukte-pogingenlockout.
        var result = await signInManager.PasswordSignInAsync(email, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            var user = await users.FindByEmailAsync(email);
            // Pending en Refused mogen een sessie hebben, maar krijgen nog geen toegang tot de toepassing.
            if (user?.AccountApprovalStatus != AccountApprovalStatus.Approved)
                return LocalRedirect(Url.Content("~/Account/Status"));
            return LocalRedirect(ReturnUrl!);
        }
        // Een algemene fout verraadt niet of het ingevoerde e-mailadres daadwerkelijk bestaat.
        ModelState.AddModelError(string.Empty, result.IsLockedOut
            ? "Aanmelden is tijdelijk geblokkeerd na te veel mislukte pogingen. Probeer later opnieuw."
            : "Aanmelden is niet gelukt. Controleer je gegevens of neem contact op met de beheerder.");
        return FailedPage();
    }

    // Alleen lokale routes mogen uit invoer komen, ook als een aanvaller returnUrl rechtstreeks post.
    private string LocalReturnUrl(string? returnUrl) => Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Content("~/");

    // Ook ModelState bevat geposte waarden; beide bronnen worden gewist voordat Razor opnieuw rendert.
    private PageResult FailedPage()
    {
        Input.Password = string.Empty;
        ModelState.SetModelValue("Input.Password", string.Empty, string.Empty);
        return Page();
    }

    /// <summary>Geen provider-, reset-, rol- of profielvelden zijn onderdeel van de login-invoer.</summary>
    public sealed class LoginInput
    {
        [Required(ErrorMessage = "Vul je e-mailadres in."), EmailAddress(ErrorMessage = "Vul een geldig e-mailadres in.")]
        public string Email { get; set; } = string.Empty;
        [Required(ErrorMessage = "Vul je wachtwoord in.")]
        public string Password { get; set; } = string.Empty;
        public bool RememberMe { get; set; }
    }
}
