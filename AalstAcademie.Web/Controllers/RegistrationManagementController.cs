using System.Globalization;
using System.Security.Claims;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AalstAcademie.Web.Controllers;

/// <summary>Actueel geautoriseerd beheer met begrensde filters en afzonderlijke CSRF-beveiligde beoordeling.</summary>
[Authorize(Policy = TrainingPolicies.TrainingManagement), Route("RegistrationManagement")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RegistrationManagementController(RegistrationManagementQueries queries, RegistrationReviewService review) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    [HttpGet("")]
    public async Task<IActionResult> Index(string? trainingId, string? momentId, string? status,
        string? registrationPage, string? waitlistPage, CancellationToken ct)
    {
        EnrolmentHttpForms.Query(Request, ModelState, "trainingId", "momentId", "status", "registrationPage", "waitlistPage");
        int? Id(string? raw, string field)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0) return value;
            ModelState.AddModelError(field, "Kies een geldig positief ID."); return null;
        }
        var training = Id(trainingId, "trainingId"); var moment = Id(momentId, "momentId");
        RegistrationStatus? filterStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            filterStatus = status.ToLowerInvariant() switch { "requested" => RegistrationStatus.Requested,
                "confirmed" => RegistrationStatus.Confirmed, "refused" => RegistrationStatus.Refused,
                "cancelled" => RegistrationStatus.Cancelled, _ => null };
            if (filterStatus is null) ModelState.AddModelError("status", "Kies een geldige registratiestatus.");
        }
        var rp = EnrolmentHttpForms.Page(registrationPage, "registrationPage", Request, ModelState);
        var wp = EnrolmentHttpForms.Page(waitlistPage, "waitlistPage", Request, ModelState);
        if (!ModelState.IsValid) { Response.StatusCode = 400; return View(new RegistrationManagementReadModel(TrainingOperationStatus.Invalid, [], [], 1, 1, 1, 1)); }
        var result = await queries.GetAsync(Actor, training, moment, filterStatus, rp, wp, ct);
        return EnrolmentHttpForms.Boundary(result.Status) ?? View(result);
    }
    [HttpGet("Details/{id:int}")]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var result = await queries.GetDetailsAsync(Actor, id, ct);
        return EnrolmentHttpForms.Boundary(result.Status) ?? View(new RegistrationReviewViewModel { Registration = result.Registration!,
            Input = new() { ExpectedRegistrationVersion = result.Registration!.RegistrationVersion, ExpectedMomentVersion = result.Registration.MomentVersion } });
    }
    [HttpPost("Confirm/{id:int}"), ValidateAntiForgeryToken]
    public Task<IActionResult> Confirm([FromRoute] int id, [FromForm, Bind("ExpectedRegistrationVersion,ExpectedMomentVersion")] RegistrationConfirmInput input, CancellationToken ct) =>
        Submit(id, new RegistrationRefuseInput { ExpectedRegistrationVersion = input.ExpectedRegistrationVersion, ExpectedMomentVersion = input.ExpectedMomentVersion }, false, ct);
    [HttpPost("Refuse/{id:int}"), ValidateAntiForgeryToken]
    public Task<IActionResult> Refuse([FromRoute] int id, [FromForm, Bind("ExpectedRegistrationVersion,ExpectedMomentVersion,DecisionReason")] RegistrationRefuseInput input, CancellationToken ct) => Submit(id, input, true, ct);
    private async Task<IActionResult> Submit(int id, RegistrationRefuseInput input, bool refuse, CancellationToken ct)
    {
        var before = await queries.GetDetailsAsync(Actor, id, ct);
        if (EnrolmentHttpForms.Boundary(before.Status) is { } denied) return denied;
        await EnrolmentHttpForms.CheckAsync(Request, ModelState, "ExpectedRegistrationVersion,ExpectedMomentVersion" + (refuse ? ",DecisionReason" : ""), ct);
        var result = ModelState.IsValid ? refuse ? await review.RefuseAsync(Actor, id, input, ct) : await review.ConfirmAsync(Actor, id, input, ct) : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded) { TempData["EnrolmentMessage"] = refuse ? "De aanvraag is geweigerd." : "De aanvraag is bevestigd."; return RedirectToAction(nameof(Details), new { id }); }
        if (EnrolmentHttpForms.Boundary(result.Status) is { } failure) return failure;
        EnrolmentHttpForms.Feedback(this, result);
        return View("Details", new RegistrationReviewViewModel { Registration = before.Registration!, Input = input, IsConflict = result.Status == TrainingOperationStatus.Conflict });
    }
}
