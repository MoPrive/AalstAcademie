// Sprint 003: Verwerkt eigen opleidingen of alle opleidingen voor een beheerder
// Scheidt metadata en doelgroepacties en bewaart raw invoer, ontbrekende veldwaarden en de oorspronkelijke versie bij fouten.
using System.Globalization;
using System.Security.Claims;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Training;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AalstAcademie.Web.Controllers;

/// <summary>
/// Beheer-HTTP voor eigen opleidingen of alle opleidingen van een actuele beheerder.
/// Formulierbinding verleent geen rechten: de query controleert scope en iedere service controleert opnieuw binnen de write-lock.
/// </summary>
[Authorize(Policy = TrainingPolicies.TrainingManagement)]
[Route("TrainingManagement")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TrainingManagementController(TrainingManagementService management,
    TrainingManagementQueries queries, TrainingAccessReader access) : Controller
{
    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>Strikte leesfilters; onbekende of lege geposte waarden worden geen stil gekozen defaults.</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(string? audience = null, string? categoryId = null,
        string? page = null, CancellationToken cancellationToken = default)
    {
        var actor = await access.ReadActorAsync(ActorId, cancellationToken);
        if (actor is null) return StatusCode(StatusCodes.Status403Forbidden);
        // Verwijderde of onbekende filterkeys worden expliciet afgewezen, ook als ze leeg zijn.
        var allowedKeys = new HashSet<string>(["audience", "categoryId", "page"], StringComparer.OrdinalIgnoreCase);
        foreach (var key in Request.Query.Keys)
            if (!allowedKeys.Contains(key) || Request.Query[key].Count != 1)
                ModelState.AddModelError(key, "Dit filter wordt niet ondersteund of is meermaals opgegeven.");
        var audienceFilter = ParseAudienceFilter(audience);
        var selectedCategory = Request.Query.ContainsKey("categoryId") && !string.IsNullOrWhiteSpace(categoryId)
            ? TrainingInputParsing.Integer(categoryId, "CategoryId", ModelState) : null;
        var selectedPage = Request.Query.ContainsKey("page") ? TrainingInputParsing.Integer(page, "Page", ModelState) : 1;
        var filter = new TrainingListFilter(audienceFilter, selectedCategory, selectedPage ?? 1);
        var referenceResult = await queries.GetReferencesAsync(ActorId, cancellationToken);
        if (!referenceResult.Succeeded) return Failure(referenceResult.Status)!;
        TrainingPageReadModel resultPage = new([], filter.Page, 1, 0, referenceResult.Value!.Categories.Count != 0);
        if (ModelState.IsValid)
        {
            var result = await queries.GetListAsync(ActorId, filter, cancellationToken);
            if (Failure(result.Status) is IActionResult failure) return failure;
            if (result.Succeeded) resultPage = result.Value!;
            else AddErrors(result.Errors);
        }
        if (!ModelState.IsValid) Response.StatusCode = StatusCodes.Status400BadRequest;
        return View(new TrainingIndexViewModel(resultPage, filter, referenceResult.Value!, actor.IsAdministrator));
    }

    /// <summary>Dezelfde geautoriseerde detailquery voorkomt dat een vreemd ID informatie vrijgeeft.</summary>
    [HttpGet("Details/{id:int}")]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var result = await queries.GetDetailsAsync(ActorId, id, cancellationToken);
        if (Failure(result.Status) is IActionResult failure) return failure;
        var actor = await access.ReadActorAsync(ActorId, cancellationToken);
        return actor is null ? StatusCode(StatusCodes.Status403Forbidden)
            : View(new TrainingDetailsViewModel(result.Value!, actor.IsAdministrator));
    }

    /// <summary>Een gewone GET-eigenaarselectie bepaalt het juiste prijsdeel; JavaScript is niet nodig.</summary>
    [HttpGet("Create")]
    public async Task<IActionResult> Create(string? ownerUserId, CancellationToken cancellationToken)
    {
        var actor = await access.ReadActorAsync(ActorId, cancellationToken);
        if (actor is null) return StatusCode(StatusCodes.Status403Forbidden);
        var input = new TrainingCreateInput { OwnerUserId = actor.IsAdministrator ? ownerUserId : null };
        var (model, failure) = await CreateModelAsync(input, actor, cancellationToken);
        if (failure is not null) return failure;
        if ((!actor.IsAdministrator || !string.IsNullOrWhiteSpace(ownerUserId)) && model!.Owner is null)
        {
            ModelState.AddModelError("OwnerUserId", "Deze lesgever voldoet momenteel niet aan de voorwaarden voor aanmaak.");
            Feedback(new(TrainingOperationStatus.Invalid), out _, out var message);
            model.Message = message;
        }
        return View(model);
    }

    /// <summary>Bindt uitsluitend invoer; extra privilegekeys en dubbele scalarvelden geven fouten vóór een write.</summary>
    [HttpPost("Create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromForm, Bind(TrainingCreateInput.BindCreate)] TrainingCreateInput input,
        CancellationToken cancellationToken)
    {
        var actor = await access.ReadActorAsync(ActorId, cancellationToken);
        if (actor is null) return StatusCode(StatusCodes.Status403Forbidden);
        await TrainingInputParsing.CheckFormAsync(Request, ModelState,
            actor.IsAdministrator ? TrainingCreateInput.BindCreate : TrainingFieldsInput.BindFields, cancellationToken);
        var fields = TrainingInputParsing.Fields(input, ModelState);
        var result = ModelState.IsValid
            ? await management.CreateAsync(ActorId, new(fields, actor.IsAdministrator ? input.OwnerUserId : null), cancellationToken)
            : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded)
        {
            TempData["TrainingMessage"] = "De opleiding is aangemaakt.";
            return RedirectToAction(nameof(Details), new { id = result.Id });
        }
        if (Failure(result.Status) is IActionResult failure) return failure;
        var (model, modelFailure) = await CreateModelAsync(input, actor, cancellationToken);
        if (modelFailure is not null) return modelFailure;
        Feedback(result, out var conflict, out var message);
        model!.IsConflict = conflict; model.Message = message;
        return View(model);
    }

    /// <summary>Metadata blijft wijzigbaar; historische deelname vergrendelt uitsluitend gewijzigde voorwaarden.</summary>
    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var result = await queries.GetDetailsAsync(ActorId, id, cancellationToken);
        if (Failure(result.Status) is IActionResult failure) return failure;
        var details = result.Value!;
        var input = EditInput(details);
        var (model, modelFailure) = await EditModelAsync(input, details, cancellationToken);
        if (modelFailure is not null) return modelFailure;
        if (!details.CanEdit)
        {
            Feedback(new(TrainingOperationStatus.Conflict), out var conflict, out var message, details);
            model!.IsConflict = conflict; model.Message = message;
        }
        return View(model);
    }

    /// <summary>Een gewone edit bindt alleen metadata; de aanwezigheid van voorwaarden komt uit echte formkeys.</summary>
    [HttpPost("Edit/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [FromForm, Bind(TrainingEditInput.BindEdit)] TrainingEditInput input,
        CancellationToken cancellationToken)
    {
        // Eerst scope lezen: een vreemde/missing rij blijft 404, ook bij foutieve formulierwaarden.
        var before = await queries.GetDetailsAsync(ActorId, id, cancellationToken);
        if (Failure(before.Status) is IActionResult failure) return failure;
        await TrainingInputParsing.CheckFormAsync(Request, ModelState, TrainingEditInput.BindEdit, cancellationToken);
        var version = TrainingInputParsing.Version(input.ExpectedVersion, ModelState);
        var fields = TrainingInputParsing.Fields(input, ModelState);
        // Omitted prijs/motivatie is een patch, geen null/false-opdracht. Deze flags zijn nooit bindbare input.
        var form = Request.HasFormContentType ? await Request.ReadFormAsync(cancellationToken) : null;
        var result = ModelState.IsValid ? await management.UpdateAsync(ActorId,
            new(id, version, fields, form?.ContainsKey("ExternalTotalPriceEuros") == true,
                form?.ContainsKey("RequiresMotivation") == true), cancellationToken)
            : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded)
        {
            TempData["TrainingMessage"] = "De opleiding is gewijzigd.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (Failure(result.Status) is IActionResult resultFailure) return resultFailure;
        var current = await queries.GetDetailsAsync(ActorId, id, cancellationToken);
        if (Failure(current.Status) is IActionResult currentFailure) return currentFailure;
        var (model, modelFailure) = await EditModelAsync(input, current.Value!, cancellationToken);
        if (modelFailure is not null) return modelFailure;
        Feedback(result, out var conflict, out var message, current.Value);
        model!.IsConflict = conflict; model.Message = message;
        return View(model);
    }

    /// <summary>Alleen een huidige admin krijgt doelgroepgegevens; de GET is geen mutatie.</summary>
    [HttpGet("Audience/{id:int}"), Authorize(Policy = AccountPolicies.ApprovedAdministrator)]
    public async Task<IActionResult> Audience(int id, CancellationToken cancellationToken)
    {
        if ((await access.ReadActorAsync(ActorId, cancellationToken))?.IsAdministrator != true)
            return StatusCode(StatusCodes.Status403Forbidden);
        var result = await queries.GetDetailsAsync(ActorId, id, cancellationToken);
        if (Failure(result.Status) is IActionResult failure) return failure;
        var details = result.Value!;
        var input = new TrainingAudienceInput
        {
            ExpectedVersion = details.Training.Version.ToString(), AudienceScope = details.Training.AudienceScope.ToString(),
            DepartmentIds = details.Departments.Select(x => x.Id.ToString(CultureInfo.InvariantCulture)).ToArray()
        };
        var (model, modelFailure) = await AudienceModelAsync(input, details, cancellationToken);
        if (modelFailure is not null) return modelFailure;
        if (!details.CanAssignAudience)
        {
            Feedback(new(TrainingOperationStatus.Conflict), out var conflict, out var message, details);
            model!.IsConflict = conflict; model.Message = message;
        }
        return View(model);
    }

    /// <summary>De volledige gewenste scope/linkset gaat naar één servicecommand, zonder stille deduplicatie.</summary>
    [HttpPost("Audience/{id:int}"), ValidateAntiForgeryToken, Authorize(Policy = AccountPolicies.ApprovedAdministrator)]
    public async Task<IActionResult> Audience(int id, [FromForm, Bind(TrainingAudienceInput.BindAudience)] TrainingAudienceInput input,
        CancellationToken cancellationToken)
    {
        if ((await access.ReadActorAsync(ActorId, cancellationToken))?.IsAdministrator != true)
            return StatusCode(StatusCodes.Status403Forbidden);
        var before = await queries.GetDetailsAsync(ActorId, id, cancellationToken);
        if (Failure(before.Status) is IActionResult failure) return failure;
        await TrainingInputParsing.CheckFormAsync(Request, ModelState, TrainingAudienceInput.BindAudience, cancellationToken);
        var version = TrainingInputParsing.Version(input.ExpectedVersion, ModelState);
        var scope = TrainingInputParsing.Audience(input.AudienceScope, ModelState, required: true);
        var departments = TrainingInputParsing.Departments(input.DepartmentIds, ModelState);
        var result = ModelState.IsValid ? await management.AssignAudienceAsync(ActorId,
            new(id, version, scope!.Value, departments), cancellationToken) : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded)
        {
            TempData["TrainingMessage"] = "De doelgroep is gewijzigd.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (Failure(result.Status) is IActionResult resultFailure) return resultFailure;
        var current = await queries.GetDetailsAsync(ActorId, id, cancellationToken);
        if (Failure(current.Status) is IActionResult currentFailure) return currentFailure;
        var (model, modelFailure) = await AudienceModelAsync(input, current.Value!, cancellationToken);
        if (modelFailure is not null) return modelFailure;
        Feedback(result, out var conflict, out var message, current.Value);
        model!.IsConflict = conflict; model.Message = message;
        return View(model);
    }

    /// <summary>Ververst alleen referenties en huidige eligibility; oorspronkelijke input/token wordt nooit vervangen.</summary>
    private async Task<(TrainingCreateViewModel? Model, IActionResult? Failure)> CreateModelAsync(TrainingCreateInput input,
        TrainingActor actor, CancellationToken cancellationToken)
    {
        var references = await queries.GetReferencesAsync(ActorId, cancellationToken);
        if (!references.Succeeded) return (null, Failure(references.Status));
        IReadOnlyList<TrainingOwnerReadModel> owners = [];
        TrainingOwnerReadModel? owner;
        if (actor.IsAdministrator)
        {
            var result = await queries.GetEligibleOwnersAsync(ActorId, cancellationToken);
            if (!result.Succeeded) return (null, Failure(result.Status));
            owners = result.Value!;
            owner = owners.SingleOrDefault(x => x.UserId == input.OwnerUserId);
        }
        else owner = await access.ReadEligibleOwnerAsync(ActorId, cancellationToken);
        var model = new TrainingCreateViewModel
        {
            IsAdministrator = actor.IsAdministrator, Owner = owner, Owners = owners, References = references.Value!,
            OwnerUserId = input.OwnerUserId
        };
        TrainingInputParsing.CopyFields(input, model);
        return (model, null);
    }

    /// <summary>Combineert behouden editinvoer met verse referenties zonder de gelezen versie te overschrijven.</summary>
    private async Task<(TrainingEditViewModel? Model, IActionResult? Failure)> EditModelAsync(TrainingEditInput input,
        TrainingDetailsReadModel details, CancellationToken cancellationToken)
    {
        var references = await queries.GetReferencesAsync(ActorId, cancellationToken);
        if (!references.Succeeded) return (null, Failure(references.Status));
        var model = new TrainingEditViewModel { Details = details, References = references.Value!, ExpectedVersion = input.ExpectedVersion };
        TrainingInputParsing.CopyFields(input, model);
        return (model, null);
    }

    /// <summary>De gewenste linkset blijft de POST-set, ook wanneer actuele afdelingen intussen veranderd zijn.</summary>
    private async Task<(TrainingAudienceViewModel? Model, IActionResult? Failure)> AudienceModelAsync(TrainingAudienceInput input,
        TrainingDetailsReadModel details, CancellationToken cancellationToken)
    {
        var references = await queries.GetReferencesAsync(ActorId, cancellationToken);
        if (!references.Succeeded) return (null, Failure(references.Status));
        return (new TrainingAudienceViewModel { Details = details, References = references.Value!, ExpectedVersion = input.ExpectedVersion,
            AudienceScope = input.AudienceScope, DepartmentIds = input.DepartmentIds }, null);
    }

    /// <summary>Vult uitsluitend een verse GET; na een fout blijft de oorspronkelijke POST-invoer leidend.</summary>
    private static TrainingEditInput EditInput(TrainingDetailsReadModel details)
    {
        var row = details.Training;
        return new() { Title = row.Title, Description = details.Description, CategoryId = row.CategoryId.ToString(CultureInfo.InvariantCulture),
            ExternalTotalPriceEuros = row.ExternalTotalPriceEuros?.ToString("0.00", CultureInfo.InvariantCulture),
            RequiresMotivation = row.RequiresMotivation, ExpectedVersion = row.Version.ToString() };
    }

    /// <summary>Een service-Forbidden blijft echte HTML403, ook wanneer rechten na de policy zijn ingetrokken.</summary>
    private IActionResult? Failure(TrainingOperationStatus status) => status switch
    {
        TrainingOperationStatus.Forbidden => StatusCode(StatusCodes.Status403Forbidden),
        TrainingOperationStatus.NotFound => NotFound(),
        _ => null
    };

    /// <summary>400/409/503 hergebruiken ModelState; nooit Clear(), een nieuwe token of automatische retry.</summary>
    private void Feedback(TrainingOperationResult result, out bool conflict, out string message,
        TrainingDetailsReadModel? details = null)
    {
        conflict = result.Status == TrainingOperationStatus.Conflict;
        message = result.Status switch
        {
            TrainingOperationStatus.Conflict => "Deze opleiding werd intussen gewijzigd of de gewenste voorwaarden zijn door deelnamehistorie vergrendeld. Herlaad de gegevens bewust vóór een nieuwe actie.",
            TrainingOperationStatus.StorageUnavailable => "De gegevens konden tijdelijk niet worden opgeslagen. Probeer later opnieuw.",
            _ => "Controleer de ingevulde velden."
        };
        Response.StatusCode = result.Status switch
        {
            TrainingOperationStatus.Conflict => StatusCodes.Status409Conflict,
            TrainingOperationStatus.StorageUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };
        AddErrors(result.Errors);
        if (conflict || result.Status == TrainingOperationStatus.StorageUnavailable || ModelState.IsValid)
            ModelState.AddModelError(string.Empty, message);
    }

    /// <summary>Veldnamen sluiten aan op flat inputnamen, zodat Razor feedback en raw invoer samen kan tonen.</summary>
    private void AddErrors(IReadOnlyDictionary<string, string[]>? errors)
    {
        foreach (var (field, messages) in errors ?? new Dictionary<string, string[]>())
            foreach (var message in messages) ModelState.AddModelError(field, message);
    }

    /// <summary>Geen numerieke enumfallback in URL-filters; de vier leesbare querywaarden zijn het contract.</summary>
    private TrainingAudienceFilter ParseAudienceFilter(string? raw)
    {
        if (!Request.Query.ContainsKey("audience")) return TrainingAudienceFilter.All;
        var value = raw?.ToLowerInvariant() switch
        {
            "all" => TrainingAudienceFilter.All, "unassigned" => TrainingAudienceFilter.Unassigned,
            "alldepartments" => TrainingAudienceFilter.AllDepartments, "selecteddepartments" => TrainingAudienceFilter.SelectedDepartments,
            _ => (TrainingAudienceFilter?)null
        };
        if (value is null) ModelState.AddModelError("Audience", "Kies een geldig doelgroepfilter.");
        return value ?? TrainingAudienceFilter.All;
    }
}
