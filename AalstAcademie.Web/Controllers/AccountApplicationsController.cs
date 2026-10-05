using System.Security.Claims;
using AalstAcademie.Web.Models.Accounts;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AalstAcademie.Web.Controllers;

/// <summary>Alle lees- en schrijfpunten vereisen huidige beheerrechten, niet alleen een cookie-rolclaim.</summary>
[Authorize(Policy = AccountPolicies.ApprovedAdministrator)]
[Route("Admin/AccountApplications")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountApplicationsController(AccountApplicationQueries queries, AccountReviewService reviews) : Controller
{
    /// <summary>Rendert de eerste gefilterde pagina op de server, zodat gewone links ook zonder JavaScript werken.</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(AccountApprovalStatus status = AccountApprovalStatus.Pending, int page = 1,
        CancellationToken cancellationToken = default) =>
        View(new AccountApplicationsIndexViewModel(await queries.GetSnapshotAsync(status, page, cancellationToken)));

    /// <summary>Levert dezelfde actuele teller en lijst als JSON voor de live browserverversing.</summary>
    [HttpGet("Snapshot")]
    public async Task<IActionResult> Snapshot(AccountApprovalStatus status = AccountApprovalStatus.Pending, int page = 1,
        CancellationToken cancellationToken = default) =>
        Json(await queries.GetSnapshotAsync(status, page, cancellationToken));

    /// <summary>Laadt de aanvraag en de toegestane beoordelingen voor de aangemelde beheerder.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> Details(string id, CancellationToken cancellationToken)
    {
        var model = await queries.GetDetailsAsync(id, User.FindFirstValue(ClaimTypes.NameIdentifier)!, cancellationToken);
        return model is null ? NotFound() : View(model);
    }

    // Er bestaan geen muterende GET-routes. Antiforgery controleert ieder gewoon HTML-formulier.
    // Bind laat uitsluitend het gelezen versietoken en de twee beoordelingsvelden toe; de actie komt uit de route.
    [HttpPost("{id}/Approve"), ValidateAntiForgeryToken]
    public Task<IActionResult> Approve(string id, [Bind("ExpectedConcurrencyStamp,DepartmentId,RefusalReason")] AccountReviewInput input,
        CancellationToken cancellationToken) => ReviewAsync(id, input, AccountReviewAction.Approve, cancellationToken);

    [HttpPost("{id}/Refuse"), ValidateAntiForgeryToken]
    public Task<IActionResult> Refuse(string id, [Bind("ExpectedConcurrencyStamp,DepartmentId,RefusalReason")] AccountReviewInput input,
        CancellationToken cancellationToken) => ReviewAsync(id, input, AccountReviewAction.Refuse, cancellationToken);

    [HttpPost("{id}/Reopen"), ValidateAntiForgeryToken]
    public Task<IActionResult> Reopen(string id, [Bind("ExpectedConcurrencyStamp,DepartmentId,RefusalReason")] AccountReviewInput input,
        CancellationToken cancellationToken) => ReviewAsync(id, input, AccountReviewAction.Reopen, cancellationToken);

    /// <summary>De actor komt van de serverprincipal; een geposte actor, rol of status wordt nooit gebruikt.</summary>
    private async Task<IActionResult> ReviewAsync(string id, AccountReviewInput input, AccountReviewAction action,
        CancellationToken cancellationToken)
    {
        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        if (!ModelState.IsValid)
        {
            // Een onleesbare afdeling wordt door modelbinding null. Laat de service
            // dat nooit als "gebruik de opgeslagen afdeling" interpreteren: geen
            // beoordeling uitvoeren en de oorspronkelijke invoer/versie behouden.
            var invalidInputModel = await queries.GetDetailsAsync(id, actorId, cancellationToken);
            if (invalidInputModel is null) return NotFound();
            invalidInputModel.Input = input;
            return View("Details", invalidInputModel);
        }
        var result = await reviews.ReviewAsync(actorId,
            new(id, input.ExpectedConcurrencyStamp, action, input.DepartmentId, input.RefusalReason), cancellationToken);
        if (result.Succeeded)
        {
            // PRG voorkomt dat browser-refresh de opgeslagen beslissing nogmaals verstuurt.
            TempData["ReviewMessage"] = "De beoordeling is opgeslagen. De aanvrager meldt zich opnieuw aan om de nieuwe status te zien.";
            return RedirectToAction(nameof(Details), new { id });
        }
        // De service controleert rechten opnieuw tijdens de mutatie, ook als ze na autorisatie zijn ingetrokken.
        if (result.Status == AccountOperationStatus.Forbidden) return Forbid();
        if (result.Status == AccountOperationStatus.NotFound) return NotFound();
        var model = await queries.GetDetailsAsync(id, actorId, cancellationToken);
        if (model is null) return NotFound();
        if (result.Status == AccountOperationStatus.Conflict)
        {
            // Geen automatische retry met een nieuwe versie: eerst opnieuw lezen en bewust beoordelen.
            ModelState.Clear();
            model.IsConflict = true;
            Response.StatusCode = StatusCodes.Status409Conflict;
            ModelState.AddModelError(string.Empty, "Deze aanvraag is inmiddels gewijzigd. Lees de huidige gegevens opnieuw vóór een nieuwe beoordeling.");
        }
        else
        {
            // Het oorspronkelijke versietoken en ingevoerde waarden blijven bij validatiefouten behouden.
            model.Input = input;
            foreach (var error in result.Errors ?? new Dictionary<string, string>())
                ModelState.AddModelError(error.Key, error.Value);
        }
        return View("Details", model);
    }
}
