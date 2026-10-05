using System.Security.Claims;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AalstAcademie.Web.Controllers;

/// <summary>Afzonderlijke CSRF-beveiligde opdrachten; actor, status, prijs en tijd zijn nooit formulierinvoer.</summary>
[Authorize(Policy = EmployeePolicies.EmployeeParticipation), Route("EmployeeEnrolments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class EmployeeEnrolmentsController(EmployeeEnrolmentService service, EmployeeCatalogueQueries queries) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    [HttpPost("Request/{momentId:int}"), ValidateAntiForgeryToken]
    public Task<IActionResult> RequestPlace([FromRoute] int momentId,
        [FromForm, Bind("ExpectedTrainingVersion,ExpectedMomentVersion,Motivation")] EmployeeRequestInput input, CancellationToken ct) =>
        Submit(momentId, new EmployeeWaitlistInput { ExpectedTrainingVersion = input.ExpectedTrainingVersion,
            ExpectedMomentVersion = input.ExpectedMomentVersion, Motivation = input.Motivation }, false, ct);

    [HttpPost("JoinWaitlist/{momentId:int}"), ValidateAntiForgeryToken]
    public Task<IActionResult> JoinWaitlist([FromRoute] int momentId,
        [FromForm, Bind("ExpectedTrainingVersion,ExpectedMomentVersion,Motivation,Consent")] EmployeeWaitlistInput input, CancellationToken ct) =>
        Submit(momentId, input, true, ct);

    private async Task<IActionResult> Submit(int id, EmployeeWaitlistInput input, bool join, CancellationToken ct)
    {
        // Bekende maar ontoegankelijke IDs blijven 403; ontbrekende IDs blijven 404, ook bij foutinvoer.
        var before = await queries.GetMomentAsync(Actor, id, ct);
        if (EnrolmentHttpForms.Boundary(before.Status) is { } denied) return denied;
        await EnrolmentHttpForms.CheckAsync(Request, ModelState,
            "ExpectedTrainingVersion,ExpectedMomentVersion,Motivation" + (join ? ",Consent" : ""), ct);
        var result = ModelState.IsValid
            ? join ? await service.JoinAsync(Actor, id, input, ct) : await service.RequestAsync(Actor, id, input, ct)
            : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded)
        {
            TempData["EnrolmentMessage"] = join ? "Je bent bewust aangesloten op de opleidingswachtlijst." : "Je aanvraag is ingediend en wacht op beoordeling.";
            return RedirectToAction("Index", "MyRegistrations");
        }
        if (EnrolmentHttpForms.Boundary(result.Status) is { } failure) return failure;
        var current = await queries.GetMomentAsync(Actor, id, ct);
        if (EnrolmentHttpForms.Boundary(current.Status) is { } currentFailure) return currentFailure;
        EnrolmentHttpForms.Feedback(this, result);
        // Zelfs als het moment intussen gestart is en de query geen detail meer geeft, blijven POST-invoer/tokens intact.
        return View("~/Views/EmployeeCatalogue/Moment.cshtml", new EmployeeMomentViewModel
        { MomentId = id, Moment = current.Moment, Input = input, Join = join, IsPost = true,
            IsConflict = result.Status == TrainingOperationStatus.Conflict });
    }
}
