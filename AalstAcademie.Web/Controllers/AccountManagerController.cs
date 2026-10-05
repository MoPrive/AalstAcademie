using System.Security.Claims;
using AalstAcademie.Web.Models.Accounts;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Accounts;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AalstAcademie.Web.Controllers;

/// <summary>Het eigen interne account wijzigt uitsluitend de optionele leidinggevende; geen andere identiteit bindbaar.</summary>
[Authorize(Policy = AccountPolicies.ApprovedAccount), Route("AccountManager")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountManagerController(AccountManagerService service) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var model = await service.GetAsync(Actor, ct);
        return model is null ? StatusCode(403) : View(model);
    }
    [HttpPost(""), ValidateAntiForgeryToken]
    public async Task<IActionResult> Index([FromForm, Bind("ManagerUserId,ExpectedConcurrencyStamp")] AccountManagerInput input, CancellationToken ct)
    {
        if (await service.GetAsync(Actor, ct) is null) return StatusCode(403);
        await EnrolmentHttpForms.CheckAsync(Request, ModelState, "ManagerUserId,ExpectedConcurrencyStamp", ct);
        var result = ModelState.IsValid ? await service.ChangeAsync(Actor, input, ct) : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded)
        {
            TempData["EnrolmentMessage"] = "Je leidinggevende is opgeslagen.";
            return RedirectToAction(nameof(Index));
        }
        if (EnrolmentHttpForms.Boundary(result.Status) is { } failure) return failure;
        var model = await service.GetAsync(Actor, ct);
        if (model is null) return StatusCode(403);
        model.Input = input; // Oude stamp en keuze behouden; alleen de beschikbare namen worden herlezen.
        EnrolmentHttpForms.Feedback(this, result);
        return View(model);
    }
}
