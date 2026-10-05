using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Areas.Identity.Pages.Account;

/// <summary>Vervangt de packaged registratie: alleen de whitelisted command kan nieuwe aanvraaggegevens leveren.</summary>
[AllowAnonymous]
public sealed class RegisterModel(DemoMode demoMode, ApplicationDbContext context, AccountRegistrationService registrations,
    UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signInManager, AccountManagerChoices managerChoices) : PageModel
{
    // Geen ApplicationUser als bindmodel: rollen, stamps, goedkeuring en DemoSeedKey zijn niet bindbaar.
    [BindProperty]
    public AccountRegistrationCommand Input { get; set; } = new();
    // De zichtbaarheid en POST-grens gebruiken dezelfde reeds gevalideerde demo-instelling.
    public bool RegistrationAvailable => demoMode.IsEnabled;
    public IReadOnlyList<SelectListItem> Departments { get; private set; } = [];
    public IReadOnlyList<SelectListItem> Managers { get; private set; } = [];

    /// <summary>Laadt alleen bij toegestane registratie de bestaande afdelingen als keuzegegevens.</summary>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (RegistrationAvailable) await LoadDepartmentsAsync(cancellationToken);
        return Page();
    }

    /// <summary>Laat de transactieservice de aanvraag opslaan en geeft pas na succes een beperkte accountsessie.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // Buiten demo wordt geen account, mailtoken of nep-bevestigingsmail aangemaakt.
        if (!RegistrationAvailable) return Page();
        // Selectieopties zijn ook bij een fout nodig: de browser ontvangt een volledig bruikbaar formulier.
        await LoadDepartmentsAsync(cancellationToken);
        // Sprint 004: ook ongemodelleerde privilege-/legacyvelden worden expliciet geweigerd, niet stil genegeerd.
        var allowed = new HashSet<string>(new[] { "RequestedAccountType", "Email", "PhoneNumber", "Password", "ConfirmPassword",
            "FirstName", "LastName", "DepartmentId", "ManagerUserId", "OrganizationName", "VatNumber" }
            .Select(x => "Input." + x), StringComparer.OrdinalIgnoreCase);
        var form = await Request.ReadFormAsync(cancellationToken);
        foreach (var (key, values) in form)
        {
            if (key.Equals("__RequestVerificationToken", StringComparison.OrdinalIgnoreCase)) continue;
            if (!allowed.Contains(key) || values.Count != 1)
                ModelState.AddModelError(key, "Dit aanvraagveld is niet toegestaan of is meermaals opgegeven.");
        }
        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            ForgetPasswords();
            return Page();
        }
        var result = await registrations.RegisterAsync(Input, cancellationToken);
        if (result.Succeeded)
        {
            // RegisterAsync meldt pas succes na commit; een cookie kan nooit een teruggerold account vertegenwoordigen.
            var user = await users.FindByIdAsync(result.UserId!);
            if (user is null) throw new InvalidOperationException("De opgeslagen aanvraag is niet beschikbaar.");
            await signInManager.SignInAsync(user, isPersistent: false);
            return LocalRedirect(Url.Content("~/Account/Status"));
        }
        // Servicefouten worden gekoppeld aan de Razor-veldnamen, zodat de juiste veldmeldingen verschijnen.
        foreach (var error in result.Errors ?? new Dictionary<string, string>())
            ModelState.AddModelError(string.IsNullOrEmpty(error.Key) ? string.Empty : $"Input.{error.Key}", error.Value);
        if (result.Status == AccountOperationStatus.Conflict)
            ModelState.AddModelError(string.Empty, "De aanvraag kon niet worden opgeslagen. Controleer of dit e-mailadres al een account heeft.");
        if (result.Status == AccountOperationStatus.StorageUnavailable)
            ModelState.AddModelError(string.Empty, "De aanvraag kon tijdelijk niet worden opgeslagen. Probeer later opnieuw.");
        Response.StatusCode = result.Status switch
        {
            AccountOperationStatus.Conflict => StatusCodes.Status409Conflict,
            AccountOperationStatus.StorageUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };
        ForgetPasswords();
        return Page();
    }

    // Alleen lezen: AsNoTracking voorkomt onnodige EF-wijzigingsregistratie voor dropdowngegevens.
    private async Task LoadDepartmentsAsync(CancellationToken cancellationToken)
    {
        Departments = await context.Departments.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync(cancellationToken);
        Managers = (await managerChoices.GetAsync(cancellationToken)).Select(x => new SelectListItem(x.DisplayName, x.UserId ?? "")).ToArray();
    }

    /// <summary>Een fout toont overige aanvraaggegevens opnieuw, maar nooit het ingevoerde wachtwoord.</summary>
    private void ForgetPasswords()
    {
        Input = Input with { Password = null, ConfirmPassword = null };
        // Behoud veldfouten, wis alleen de attempted value die taghelpers anders zouden kunnen hergebruiken.
        ModelState.SetModelValue("Input.Password", string.Empty, string.Empty);
        ModelState.SetModelValue("Input.ConfirmPassword", string.Empty, string.Empty);
    }
}
