using System.Security.Claims;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AalstAcademie.Web.Controllers;

/// <summary>Eigen historie en concrete bufferintrekking blijven beschikbaar na alleen rol-/doelgroepverlies.</summary>
[Authorize(Policy = AccountPolicies.ApprovedAccount), Route("MyRegistrations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MyRegistrationsController(EmployeeEnrolmentQueries queries, EmployeeEnrolmentService service, RegistrationCancellationService cancellation) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    [HttpGet("")]
    public async Task<IActionResult> Index(string? registrationPage, string? waitlistPage, CancellationToken ct)
    {
        EnrolmentHttpForms.Query(Request, ModelState, "registrationPage", "waitlistPage");
        var a = EnrolmentHttpForms.Page(registrationPage, "registrationPage", Request, ModelState);
        var b = EnrolmentHttpForms.Page(waitlistPage, "waitlistPage", Request, ModelState);
        var result = await queries.GetAsync(Actor, a, b, ct);
        if (EnrolmentHttpForms.Boundary(result.Status) is { } failure) return failure;
        if (!ModelState.IsValid) Response.StatusCode = 400;
        return View(result);
    }
    [HttpGet("Cancel/{id:int}")]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        var read = await queries.GetCancelAsync(Actor, id, ct);
        if (EnrolmentHttpForms.Boundary(read.Status) is { } denied) return denied;
        if (!read.Registration!.CanCancel) EnrolmentHttpForms.Feedback(this, new(TrainingOperationStatus.Conflict));
        return View(new RegistrationCancelViewModel { Registration = read.Registration,
            Input = new() { ExpectedRegistrationVersion = read.Registration.RegistrationVersion, ExpectedMomentVersion = read.Registration.MomentVersion },
            IsConflict = !read.Registration.CanCancel });
    }
    [HttpPost("Cancel/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel([FromRoute] int id,
        [FromForm, Bind("ExpectedRegistrationVersion,ExpectedMomentVersion")] RegistrationCancelInput input, CancellationToken ct)
    {
        var read = await queries.GetCancelAsync(Actor, id, ct);
        if (EnrolmentHttpForms.Boundary(read.Status) is { } denied) return denied;
        await EnrolmentHttpForms.CheckAsync(Request, ModelState, "ExpectedRegistrationVersion,ExpectedMomentVersion", ct);
        var result = ModelState.IsValid ? await cancellation.CancelAsync(Actor, id, input, ct) : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded) { TempData["EnrolmentMessage"] = "Je aanvraag is geannuleerd."; return RedirectToAction(nameof(Index)); }
        if (EnrolmentHttpForms.Boundary(result.Status) is { } failure) return failure;
        EnrolmentHttpForms.Feedback(this, result);
        return View(new RegistrationCancelViewModel { Registration = read.Registration!, Input = input, IsConflict = result.Status == TrainingOperationStatus.Conflict });
    }
    [HttpGet("Withdraw/{id:int}")]
    public async Task<IActionResult> Withdraw(int id, CancellationToken ct)
    {
        var result = await queries.GetWithdrawAsync(Actor, id, ct);
        if (EnrolmentHttpForms.Boundary(result.Status) is { } failure) return failure;
        var closed = result.Entry!.ClosedAtUtc is not null;
        if (closed) EnrolmentHttpForms.Feedback(this, new(TrainingOperationStatus.Conflict));
        return View(new EmployeeWithdrawViewModel { Entry = result.Entry,
            Input = new() { ExpectedVersion = result.Entry.Version }, IsConflict = closed });
    }
    [HttpPost("Withdraw/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw([FromRoute] int id,
        [FromForm, Bind("ExpectedVersion")] EmployeeWithdrawInput input, CancellationToken ct)
    {
        var before = await queries.GetWithdrawAsync(Actor, id, ct);
        if (EnrolmentHttpForms.Boundary(before.Status) is { } failure) return failure;
        await EnrolmentHttpForms.CheckAsync(Request, ModelState, "ExpectedVersion", ct);
        var result = ModelState.IsValid ? await service.WithdrawAsync(Actor, id, input, ct) : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded)
        {
            TempData["EnrolmentMessage"] = "Deze aansluiting op de wachtlijst is ingetrokken.";
            return RedirectToAction(nameof(Index));
        }
        if (EnrolmentHttpForms.Boundary(result.Status) is { } denied) return denied;
        EnrolmentHttpForms.Feedback(this, result);
        // Een latere rij is nooit het doel van dit oude formulier; ID en oorspronkelijke versie blijven behouden.
        return View(new EmployeeWithdrawViewModel { Entry = before.Entry!, Input = input,
            IsConflict = result.Status == TrainingOperationStatus.Conflict });
    }
}
