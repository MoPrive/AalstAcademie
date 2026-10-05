using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using AalstAcademie.Web.Models;
using Microsoft.AspNetCore.Authorization;
using AalstAcademie.Web.Security;

namespace AalstAcademie.Web.Controllers;

/// <summary>De startpagina is alleen toegankelijk na goedkeuring; privacy en foutinformatie blijven publiek.</summary>
[Authorize(Policy = AccountPolicies.ApprovedAccount)]
public class HomeController : Controller
{
    // Een geldige login alleen volstaat hier niet: de policy controleert ook de actuele goedkeuring.
    public IActionResult Index()
    {
        return View();
    }

    // De privacyuitleg is bereikbaar voordat iemand persoonsgegevens via registratie verstrekt.
    [AllowAnonymous]
    public IActionResult Privacy()
    {
        return View();
    }

    // Foutafhandeling moet ook bij mislukte aanmelding beschikbaar zijn en bevat geen accountgegevens.
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [AllowAnonymous]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
