// Sprint 003: Verwerkt uitsluitend categorieaanmaak en hernoemen door een actuele beheerder
// Beperkte binding en antiforgery beschermen POST; de service beslist opnieuw over rechten en opslag.
using System.Security.Claims;
using AalstAcademie.Web.Models.Training;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AalstAcademie.Web.Controllers;

/// <summary>Categoriebeheer vereist actuele algemene beheerrechten en biedt uitsluitend create/rename.</summary>
[Authorize(Policy = AccountPolicies.ApprovedAdministrator)]
[Route("Categories")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CategoriesController(CategoryManagementService management, TrainingManagementQueries queries) : Controller
{
    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>Ook een lege categoriecatalogus is geldig; deze controller seedt geen voorbeelden.</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var result = await queries.GetCategoriesAsync(ActorId, cancellationToken);
        return Failure(result.Status) ?? View(new CategoryIndexViewModel(result.Value!));
    }

    /// <summary>Toont alleen een leeg naamformulier; de GET maakt geen categorie of versie aan.</summary>
    [HttpGet("Create")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        // Een ongecachete query bewaakt intussen ingetrokken rechten naast de middlewarepolicy.
        var result = await queries.GetCategoriesAsync(ActorId, cancellationToken);
        return Failure(result.Status) ?? View(new CategoryCreateViewModel());
    }

    /// <summary>De service normaliseert de naam atomair; extra opslag-/rolvelden worden niet geaccepteerd.</summary>
    [HttpPost("Create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromForm, Bind(CategoryCreateInput.BindCreate)] CategoryCreateInput input,
        CancellationToken cancellationToken)
    {
        var permitted = await queries.GetCategoriesAsync(ActorId, cancellationToken);
        if (Failure(permitted.Status) is IActionResult failure) return failure;
        await TrainingInputParsing.CheckFormAsync(Request, ModelState, CategoryCreateInput.BindCreate, cancellationToken);
        var result = ModelState.IsValid ? await management.CreateAsync(ActorId, new(input.Name), cancellationToken)
            : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded)
        {
            TempData["CategoryMessage"] = "De categorie is aangemaakt.";
            return RedirectToAction(nameof(Index));
        }
        if (Failure(result.Status) is IActionResult resultFailure) return resultFailure;
        Feedback(result, out var conflict, out var message);
        return View(new CategoryCreateViewModel { Name = input.Name, IsConflict = conflict, Message = message });
    }

    /// <summary>ShadowVersion wordt expliciet gelezen als formulier-token; de Training-versies blijven onaangeroerd.</summary>
    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var result = await queries.GetCategoryAsync(ActorId, id, cancellationToken);
        return Failure(result.Status) ?? View(new CategoryEditViewModel
            { Id = id, Name = result.Value!.Name, ExpectedVersion = result.Value.Version.ToString() });
    }

    /// <summary>Een fout of conflict toont de ingevoerde naam en oorspronkelijke versie, zonder automatische herpost.</summary>
    [HttpPost("Edit/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [FromForm, Bind(CategoryEditInput.BindEdit)] CategoryEditInput input,
        CancellationToken cancellationToken)
    {
        var permitted = await queries.GetCategoryAsync(ActorId, id, cancellationToken);
        if (Failure(permitted.Status) is IActionResult failure) return failure;
        await TrainingInputParsing.CheckFormAsync(Request, ModelState, CategoryEditInput.BindEdit, cancellationToken);
        var version = TrainingInputParsing.Version(input.ExpectedVersion, ModelState);
        var result = ModelState.IsValid ? await management.RenameAsync(ActorId, new(id, version, input.Name), cancellationToken)
            : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded)
        {
            TempData["CategoryMessage"] = "De categorie is hernoemd.";
            return RedirectToAction(nameof(Index));
        }
        if (Failure(result.Status) is IActionResult resultFailure) return resultFailure;
        Feedback(result, out var conflict, out var message);
        return View(new CategoryEditViewModel { Id = id, Name = input.Name, ExpectedVersion = input.ExpectedVersion,
            IsConflict = conflict, Message = message });
    }

    /// <summary>De service kan rechten opnieuw afwijzen; dit wordt echte 403 zonder AccessDenied-redirect.</summary>
    private IActionResult? Failure(TrainingOperationStatus status) => status switch
    {
        TrainingOperationStatus.Forbidden => StatusCode(StatusCodes.Status403Forbidden),
        TrainingOperationStatus.NotFound => NotFound(),
        _ => null
    };

    /// <summary>400,409,503 bevatten alleen veilige uitleg; ModelState en het oude token blijven intact.</summary>
    private void Feedback(TrainingOperationResult result, out bool conflict, out string message)
    {
        conflict = result.Status == TrainingOperationStatus.Conflict;
        message = result.Status switch
        {
            TrainingOperationStatus.Conflict => "Deze categorie werd intussen gewijzigd of het versietoken ontbreekt. Herlaad de gegevens bewust vóór een nieuwe wijziging.",
            TrainingOperationStatus.StorageUnavailable => "De categorie kon tijdelijk niet worden opgeslagen. Probeer later opnieuw.",
            _ => "Controleer de ingevulde naam."
        };
        Response.StatusCode = result.Status switch
        {
            TrainingOperationStatus.Conflict => StatusCodes.Status409Conflict,
            TrainingOperationStatus.StorageUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };
        foreach (var (field, messages) in result.Errors ?? new Dictionary<string, string[]>())
            foreach (var error in messages) ModelState.AddModelError(field, error);
        if (conflict || result.Status == TrainingOperationStatus.StorageUnavailable || ModelState.IsValid)
            ModelState.AddModelError(string.Empty, message);
    }
}
