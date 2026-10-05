using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AalstAcademie.Web.Areas.Identity.Pages.Account;

/// <summary>Veilige generieke ontvangstpagina: een URL met e-mail bewijst geen accountbezit.</summary>
[AllowAnonymous]
public sealed class RegisterConfirmationModel : PageModel
{
    // Geen lookup, mailzender of bevestigingstoken; eigen aanvraaggegevens staan alleen achter de statuspolicy.
    // De lege handler toont uitsluitend vaste uitleg, ongeacht queryparameters in de URL.
    public void OnGet() { }
}
