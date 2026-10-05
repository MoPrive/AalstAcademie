using System.Security.Claims;
using AalstAcademie.Web.Models.Accounts;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Accounts;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AalstAcademie.Web.Controllers;

/// <summary>Een beheerder wijzigt één bestaande afdelings-FK met nullable-CAS; dit scherm biedt geen afdelings-CRUD.</summary>
[Authorize(Policy = AccountPolicies.ApprovedAdministrator), Route("DepartmentResponsibles")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DepartmentResponsiblesController(DepartmentResponsibleQueries queries, DepartmentResponsibleService service) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var model = await queries.GetAsync(Actor, ct);
        return model is null ? StatusCode(403) : View(model);
    }
    [HttpPost("Assign/{id:int}"), ValidateAntiForgeryToken]
    // Het route-ID blijft het doel; een extra formulier-Id wordt door de whitelist afgewezen.
    public async Task<IActionResult> Assign([FromRoute] int id,
        [FromForm, Bind("ResponsibleUserId,ExpectedResponsibleUserId")] DepartmentResponsibleInput input, CancellationToken ct)
    {
        var model = await queries.GetAsync(Actor, ct);
        if (model is null) return StatusCode(403);
        if (!model.Departments.Any(x => x.Id == id)) return NotFound();
        await EnrolmentHttpForms.CheckAsync(Request, ModelState, "ResponsibleUserId,ExpectedResponsibleUserId", ct);
        var form = await Request.ReadFormAsync(ct);
        // Een ontbrekende oorspronkelijke waarde is geen bewijs dat de gebruiker bewust Geen zag.
        if (!form.ContainsKey("ExpectedResponsibleUserId"))
            ModelState.AddModelError("ExpectedResponsibleUserId", "Lees de oorspronkelijke verantwoordelijke opnieuw.");
        var result = ModelState.IsValid ? await service.AssignAsync(Actor, id, input, ct) : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded)
        {
            TempData["EnrolmentMessage"] = "De verantwoordelijke is opgeslagen.";
            return RedirectToAction(nameof(Index));
        }
        if (EnrolmentHttpForms.Boundary(result.Status) is { } failure) return failure;
        model = await queries.GetAsync(Actor, ct);
        if (model is null) return StatusCode(403);
        model.FailedDepartmentId = id; model.FailedInput = input;
        EnrolmentHttpForms.Feedback(this, result);
        return View("Index", model);
    }
}
