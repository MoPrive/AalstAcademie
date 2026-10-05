// Sprint 003: Verbindt beheerformulieren met scoped zaalqueries en transactionele mutaties
// GET leest of bevestigt; POST controleert whitelist, antiforgery en de oorspronkelijke versie.
using System.Globalization;
using System.Security.Claims;
using AalstAcademie.Web.Models.Training;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AalstAcademie.Web.Controllers;

/// <summary>Zaalbeheer leest actuele beheerrechten en hercontroleert versie, capaciteit en referenties bij iedere POST.</summary>
[Authorize(Policy = AccountPolicies.ApprovedAdministrator), Route("Locations")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LocationsController(LocationManagementService management, LocationManagementQueries queries) : Controller
{
    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken token)
    {
        var read = await queries.GetListAsync(ActorId, token);
        return Failure(read.Status) ?? View(new LocationIndexViewModel(read.Value!));
    }
    /// <summary>GET leest alleen; ook het aanmaakformulier vereist actuele DB-rechten.</summary>
    [HttpGet("Create")]
    public async Task<IActionResult> Create(CancellationToken token)
    {
        var read = await queries.GetListAsync(ActorId, token);
        return Failure(read.Status) ?? View(new LocationCreateViewModel());
    }
    [HttpPost("Create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromForm, Bind(LocationCreateInput.BindCreate)] LocationCreateInput input, CancellationToken token)
    {
        var read = await queries.GetListAsync(ActorId, token);
        if (Failure(read.Status) is IActionResult denied) return denied;
        await TrainingInputParsing.CheckFormAsync(Request, ModelState, LocationCreateInput.BindCreate, token);
        var fields = new LocationFields(input.Name, input.Address, MomentInputParsing.Positive(input.MaximumCapacity, "MaximumCapacity", ModelState));
        var result = ModelState.IsValid ? await management.CreateAsync(ActorId, new(fields), token) : new(TrainingOperationStatus.Invalid);
        if (result.Succeeded) return RedirectToAction(nameof(Index));
        if (Failure(result.Status) is IActionResult failure) return failure;
        Feedback(result);
        return View(new LocationCreateViewModel { Name = input.Name, Address = input.Address, MaximumCapacity = input.MaximumCapacity });
    }
    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken token)
    {
        var read = await queries.GetDetailsAsync(ActorId, id, token);
        return Failure(read.Status) ?? View(new LocationEditViewModel { Details = read.Value!, Name = read.Value!.Name,
            Address = read.Value.Address, MaximumCapacity = read.Value.MaximumCapacity.ToString(CultureInfo.InvariantCulture), ExpectedVersion = read.Value.Version.ToString() });
    }
    /// <summary>Herweergave gebruikt de oorspronkelijke invoer en versie; actuele minimumfeedback is geen automatische retry.</summary>
    [HttpPost("Edit/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [FromForm, Bind(LocationEditInput.BindEdit)] LocationEditInput input, CancellationToken token)
    {
        var read = await queries.GetDetailsAsync(ActorId, id, token);
        if (Failure(read.Status) is IActionResult denied) return denied;
        await TrainingInputParsing.CheckFormAsync(Request, ModelState, LocationEditInput.BindEdit, token);
        var version = TrainingInputParsing.Version(input.ExpectedVersion, ModelState);
        var fields = new LocationFields(input.Name, input.Address, MomentInputParsing.Positive(input.MaximumCapacity, "MaximumCapacity", ModelState));
        var result = ModelState.IsValid ? await management.UpdateAsync(ActorId, new(id, version, fields), token) : new(TrainingOperationStatus.Invalid);
        if (result.Succeeded) return RedirectToAction(nameof(Index));
        if (Failure(result.Status) is IActionResult failure) return failure;
        Feedback(result);
        return View(new LocationEditViewModel { Details = read.Value!, Name = input.Name, Address = input.Address,
            MaximumCapacity = input.MaximumCapacity, ExpectedVersion = input.ExpectedVersion });
    }
    /// <summary>Een referentie, ook historisch/geannuleerd, maakt de bevestiging readonly en geeft409 zonder write.</summary>
    [HttpGet("Delete/{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken token)
    {
        var read = await queries.GetDetailsAsync(ActorId, id, token);
        if (Failure(read.Status) is IActionResult denied) return denied;
        if (!read.Value!.CanDelete) Feedback(new(TrainingOperationStatus.Conflict));
        return View(new LocationDeleteViewModel { Details = read.Value, ExpectedVersion = read.Value.Version.ToString() });
    }
    [HttpPost("Delete/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, [FromForm, Bind(LocationDeleteInput.BindDelete)] LocationDeleteInput input, CancellationToken token)
    {
        var read = await queries.GetDetailsAsync(ActorId, id, token);
        if (Failure(read.Status) is IActionResult denied) return denied;
        await TrainingInputParsing.CheckFormAsync(Request, ModelState, LocationDeleteInput.BindDelete, token);
        var version = TrainingInputParsing.Version(input.ExpectedVersion, ModelState);
        var result = ModelState.IsValid ? await management.DeleteAsync(ActorId, new(id, version), token) : new(TrainingOperationStatus.Invalid);
        if (result.Succeeded) return RedirectToAction(nameof(Index));
        if (Failure(result.Status) is IActionResult failure) return failure;
        Feedback(result);
        return View(new LocationDeleteViewModel { Details = read.Value!, ExpectedVersion = input.ExpectedVersion });
    }
    private IActionResult? Failure(TrainingOperationStatus status) => status switch
    { TrainingOperationStatus.Forbidden => StatusCode(403), TrainingOperationStatus.NotFound => NotFound(), _ => null };
    /// <summary>Veilige HTTP-feedback behoudt ModelState; providerdetails verlaten de writegrens nooit.</summary>
    private void Feedback(TrainingOperationResult result)
    {
        Response.StatusCode = result.Status switch { TrainingOperationStatus.Conflict => 409, TrainingOperationStatus.StorageUnavailable => 503, _ => 400 };
        foreach (var (key, errors) in result.Errors ?? new Dictionary<string, string[]>())
            foreach (var error in errors) ModelState.AddModelError(key, error);
        ModelState.AddModelError("", result.Status switch
        { TrainingOperationStatus.Conflict => "De zaal is gewijzigd, heeft momentreferenties of de capaciteit is te laag. Herlees de zaal vóór een nieuwe wijziging.",
          TrainingOperationStatus.StorageUnavailable => "Opslaan is tijdelijk niet mogelijk. Probeer later opnieuw.", _ => "Controleer de zaalgegevens." });
    }
}
