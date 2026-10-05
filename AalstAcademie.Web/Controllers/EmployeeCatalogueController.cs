using System.Security.Claims;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AalstAcademie.Web.Controllers;

/// <summary>Actuele passende toekomstmomenten; iedere GET leest alleen en reserveert geen plaats.</summary>
[Authorize(Policy = EmployeePolicies.EmployeeParticipation), Route("EmployeeCatalogue")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class EmployeeCatalogueController(EmployeeCatalogueQueries queries) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    [HttpGet("")]
    public async Task<IActionResult> Index(string? categoryId, string? page, CancellationToken ct)
    {
        EnrolmentHttpForms.Query(Request, ModelState, "categoryId", "page");
        int? category = null;
        if (!string.IsNullOrEmpty(categoryId))
        {
            if (int.TryParse(categoryId, out var id) && id > 0) category = id;
            else ModelState.AddModelError("categoryId", "Kies een geldige categorie.");
        }
        var number = EnrolmentHttpForms.Page(page, "page", Request, ModelState);
        var result = await queries.GetAsync(Actor, category, number, ct);
        if (EnrolmentHttpForms.Boundary(result.Status) is { } failure) return failure;
        if (!ModelState.IsValid) Response.StatusCode = 400;
        return View(result);
    }
    [HttpGet("Moment/{id:int}")]
    public async Task<IActionResult> Moment(int id, CancellationToken ct)
    {
        var result = await queries.GetMomentAsync(Actor, id, ct);
        if (EnrolmentHttpForms.Boundary(result.Status) is { } failure) return failure;
        if (result.Status == TrainingOperationStatus.Conflict)
            EnrolmentHttpForms.Feedback(this, new(result.Status));
        var detail = result.Moment;
        return View(new EmployeeMomentViewModel { MomentId = id, Moment = detail, Join = detail?.CanJoin == true,
            IsConflict = result.Status == TrainingOperationStatus.Conflict,
            Input = new() { ExpectedTrainingVersion = detail?.TrainingVersion, ExpectedMomentVersion = detail?.MomentVersion } });
    }
}
