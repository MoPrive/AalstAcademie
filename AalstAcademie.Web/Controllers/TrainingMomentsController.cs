// Sprint 003: Verbindt momentcreate, planningedit, maximumwijziging en gewone GET-zaalkeuze met scoped queries en schrijfservices
// De server bepaalt opleiding, eigenaar en status.
using System.Globalization;
using System.Security.Claims;
using AalstAcademie.Web.Models.Training;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AalstAcademie.Web.Controllers;

/// <summary>Scoped momentbeheer. Opleiding/eigenaar/status zijn vaste servergegevens, geen formulierinvoer.</summary>
[Authorize(Policy = TrainingPolicies.TrainingManagement), Route("TrainingMoments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TrainingMomentsController(TrainingMomentManagementService management,
    TrainingMomentManagementQueries queries, TrainingManagementQueries trainings, TrainingMomentCancellationService cancellation) : Controller
{
    [HttpGet("Cancel/{id:int}"), Authorize(Policy = AccountPolicies.ApprovedAdministrator)]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        var read = await queries.GetCancelAsync(ActorId, id, ct);
        if (Failure(read.Status) is { } denied) return denied;
        if (!read.Value!.CanCancel) EnrolmentHttpForms.Feedback(this, new(TrainingOperationStatus.Conflict));
        return View(new TrainingMomentCancelViewModel { Moment = read.Value,
            Input = new() { ExpectedMomentVersion = read.Value.MomentVersion }, IsConflict = !read.Value.CanCancel });
    }
    [HttpPost("Cancel/{id:int}"), Authorize(Policy = AccountPolicies.ApprovedAdministrator), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel([FromRoute] int id, [FromForm, Bind("ExpectedMomentVersion")] TrainingMomentCancelInput input, CancellationToken ct)
    {
        var read = await queries.GetCancelAsync(ActorId, id, ct);
        if (Failure(read.Status) is { } denied) return denied;
        await EnrolmentHttpForms.CheckAsync(Request, ModelState, "ExpectedMomentVersion", ct);
        var result = ModelState.IsValid ? await cancellation.CancelAsync(ActorId, id, input, ct) : new TrainingOperationResult(TrainingOperationStatus.Invalid);
        if (result.Succeeded) { TempData["TrainingMessage"] = "Het volledige moment is geannuleerd; de betrokken deelnemers staan op de opleidingswachtlijst."; return Back(read.Value!.TrainingId); }
        if (Failure(result.Status) is { } failure) return failure;
        EnrolmentHttpForms.Feedback(this, result);
        return View(new TrainingMomentCancelViewModel { Moment = read.Value!, Input = input, IsConflict = result.Status == TrainingOperationStatus.Conflict });
    }
    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    [HttpGet("Create/{trainingId:int}")]
    public async Task<IActionResult> Create(int trainingId, CancellationToken token)
    {
        var read = await trainings.GetDetailsAsync(ActorId, trainingId, token);
        return Failure(read.Status) ?? View(new TrainingMomentCreateViewModel { TrainingId = trainingId, TrainingTitle = read.Value!.Training.Title });
    }
    /// <summary>Scope gaat vóór inputparsing; actuele vaste-eigenaar-, overlap- en zaalguards volgen in de transactie.</summary>
    [HttpPost("Create/{trainingId:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int trainingId, [FromForm, Bind(TrainingMomentCreateInput.BindCreate)] TrainingMomentCreateInput input, CancellationToken token)
    {
        var read = await trainings.GetDetailsAsync(ActorId, trainingId, token);
        if (Failure(read.Status) is IActionResult denied) return denied;
        await TrainingInputParsing.CheckFormAsync(Request, ModelState, TrainingMomentCreateInput.BindCreate, token);
        var planning = MomentInputParsing.Planning(input, ModelState);
        var maximum = MomentInputParsing.Positive(input.MaximumParticipants, "MaximumParticipants", ModelState);
        var rooms = await RoomsAsync(trainingId, planning, maximum, null, token);
        var result = ModelState.IsValid ? await management.CreateAsync(ActorId,
            new(trainingId, new(planning.Date, planning.StartTime, planning.EndTime, planning.LocationId, maximum)), token) : new(TrainingOperationStatus.Invalid);
        if (result.Succeeded) return Back(trainingId);
        if (Failure(result.Status) is IActionResult failure) return failure;
        Feedback(result);
        return View(new TrainingMomentCreateViewModel { TrainingId = trainingId, TrainingTitle = read.Value!.Training.Title,
            Date = input.Date, StartTime = input.StartTime, EndTime = input.EndTime, LocationId = input.LocationId,
            MaximumParticipants = input.MaximumParticipants, Rooms = rooms });
    }
    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id, CancellationToken token)
    {
        var read = await queries.GetDetailsAsync(ActorId, id, token);
        if (Failure(read.Status) is IActionResult denied) return denied;
        var row = read.Value!.Moment;
        var rooms = await queries.GetAvailabilityAsync(ActorId, row.TrainingId, row.Date, row.StartTime, row.EndTime, row.MaximumParticipants, id, token);
        return View(new TrainingMomentEditViewModel { Details = read.Value, Date = row.Date.ToString("yyyy-MM-dd"),
            StartTime = row.StartTime.ToString("HH:mm:ss.fffffff"), EndTime = row.EndTime.ToString("HH:mm:ss.fffffff"),
            LocationId = row.LocationId.ToString(CultureInfo.InvariantCulture), ExpectedVersion = row.Version.ToString(), Rooms = rooms.Value?.Rooms ?? [] });
    }
    /// <summary>Het bestaande maximum wordt servermatig behouden; een toekomstige geposte datum omzeilt de oude startgrens niet.</summary>
    [HttpPost("Edit/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [FromForm, Bind(TrainingMomentEditInput.BindEdit)] TrainingMomentEditInput input, CancellationToken token)
    {
        var read = await queries.GetDetailsAsync(ActorId, id, token);
        if (Failure(read.Status) is IActionResult denied) return denied;
        await TrainingInputParsing.CheckFormAsync(Request, ModelState, TrainingMomentEditInput.BindEdit, token);
        var version = TrainingInputParsing.Version(input.ExpectedVersion, ModelState);
        var fields = MomentInputParsing.Planning(input, ModelState);
        var rooms = await RoomsAsync(read.Value!.Moment.TrainingId, fields, read.Value.Moment.MaximumParticipants, id, token);
        var result = ModelState.IsValid ? await management.UpdateAsync(ActorId, new(id, version, fields), token) : new(TrainingOperationStatus.Invalid);
        if (result.Succeeded) return Back(read.Value.Moment.TrainingId);
        if (Failure(result.Status) is IActionResult failure) return failure;
        Feedback(result);
        return View(new TrainingMomentEditViewModel { Details = read.Value, Date = input.Date, StartTime = input.StartTime,
            EndTime = input.EndTime, LocationId = input.LocationId, ExpectedVersion = input.ExpectedVersion, Rooms = rooms });
    }
    [HttpGet("Capacity/{id:int}")]
    public async Task<IActionResult> Capacity(int id, CancellationToken token)
    {
        var read = await queries.GetDetailsAsync(ActorId, id, token);
        return Failure(read.Status) ?? View(new TrainingMomentCapacityViewModel { Details = read.Value!,
            MaximumParticipants = read.Value!.Moment.MaximumParticipants.ToString(CultureInfo.InvariantCulture), ExpectedVersion = read.Value.Moment.Version.ToString() });
    }
    /// <summary>Alleen maximum en oude versie zijn bindbaar; de echte bezetting en zaalcapaciteit worden opnieuw gelezen.</summary>
    [HttpPost("Capacity/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Capacity(int id, [FromForm, Bind(TrainingMomentCapacityInput.BindCapacity)] TrainingMomentCapacityInput input, CancellationToken token)
    {
        var read = await queries.GetDetailsAsync(ActorId, id, token);
        if (Failure(read.Status) is IActionResult denied) return denied;
        await TrainingInputParsing.CheckFormAsync(Request, ModelState, TrainingMomentCapacityInput.BindCapacity, token);
        var version = TrainingInputParsing.Version(input.ExpectedVersion, ModelState);
        var maximum = MomentInputParsing.Positive(input.MaximumParticipants, "MaximumParticipants", ModelState);
        var result = ModelState.IsValid ? await management.ChangeCapacityAsync(ActorId, new(id, version, maximum), token) : new(TrainingOperationStatus.Invalid);
        if (result.Succeeded) return Back(read.Value!.Moment.TrainingId);
        if (Failure(result.Status) is IActionResult failure) return failure;
        Feedback(result);
        return View(new TrainingMomentCapacityViewModel { Details = read.Value!, MaximumParticipants = input.MaximumParticipants, ExpectedVersion = input.ExpectedVersion });
    }
    /// <summary>Volledige no-JS route: een scoped leesaanvraag, gevolgd door een apart beveiligd boekingsformulier.</summary>
    [HttpGet("Availability")]
    public async Task<IActionResult> Availability(int trainingId, int? momentId, [FromQuery] TrainingMomentAvailabilityInput input, CancellationToken token)
    {
        var training = await trainings.GetDetailsAsync(ActorId, trainingId, token);
        if (Failure(training.Status) is IActionResult denied) return denied;
        TrainingMomentDetailsReadModel? moment = null;
        if (momentId is int id)
        {
            var read = await queries.GetDetailsAsync(ActorId, id, token);
            if (Failure(read.Status) is IActionResult failure) return failure;
            if (read.Value!.Moment.TrainingId != trainingId) return NotFound();
            moment = read.Value;
        }
        var date = MomentInputParsing.Date(input.Date, ModelState);
        var start = MomentInputParsing.Time(input.StartTime, "StartTime", ModelState);
        var end = MomentInputParsing.Time(input.EndTime, "EndTime", ModelState);
        var maximum = moment?.Moment.MaximumParticipants ?? MomentInputParsing.Positive(input.MaximumParticipants, "MaximumParticipants", ModelState);
        if (moment is not null) TrainingInputParsing.Version(input.ExpectedVersion, ModelState);
        var readRooms = ModelState.IsValid ? await queries.GetAvailabilityAsync(ActorId, trainingId, date, start, end, maximum, momentId, token) : new(TrainingOperationStatus.Invalid);
        if (Failure(readRooms.Status) is IActionResult failed) return failed;
        if (!readRooms.Succeeded) Feedback(new(readRooms.Status, Errors: readRooms.Errors));
        return View(new TrainingMomentAvailabilityViewModel { TrainingId = trainingId, MomentId = momentId,
            TrainingTitle = training.Value!.Training.Title, Date = input.Date, StartTime = input.StartTime, EndTime = input.EndTime,
            LocationId = input.LocationId, MaximumParticipants = maximum?.ToString(CultureInfo.InvariantCulture) ?? input.MaximumParticipants,
            ExpectedVersion = input.ExpectedVersion, CanEdit = moment?.CanEditPlanning ?? training.Value.CanCreateMoment,
            Rooms = readRooms.Value?.Rooms ?? [] });
    }
    /// <summary>Onbekende positieve zaal-ID geeft400; bekende maar intussen bezette/kleinere zalen blijven echte409-serviceconflicten.</summary>
    private async Task<IReadOnlyList<TrainingAvailabilityRoomReadModel>> RoomsAsync(int trainingId, TrainingMomentPlanningFields fields, int? maximum, int? excluded, CancellationToken token)
    {
        if (!ModelState.IsValid) return [];
        var read = await queries.GetAvailabilityAsync(ActorId, trainingId, fields.Date, fields.StartTime, fields.EndTime, maximum, excluded, token);
        if (!read.Succeeded)
        {
            foreach (var (key, errors) in read.Errors ?? new Dictionary<string, string[]>())
                foreach (var error in errors) ModelState.AddModelError(key, error);
            return [];
        }
        if (!read.Value!.Rooms.Any(x => x.Id == fields.LocationId)) ModelState.AddModelError("LocationId", "Kies een bestaande zaal.");
        return read.Value.Rooms;
    }
    private IActionResult Back(int trainingId) => RedirectToAction("Details", "TrainingManagement", new { id = trainingId });
    private IActionResult? Failure(TrainingOperationStatus status) => status switch
    { TrainingOperationStatus.Forbidden => StatusCode(403), TrainingOperationStatus.NotFound => NotFound(), _ => null };
    private void Feedback(TrainingOperationResult result)
    {
        Response.StatusCode = result.Status switch { TrainingOperationStatus.Conflict => 409, TrainingOperationStatus.StorageUnavailable => 503, _ => 400 };
        foreach (var (key, errors) in result.Errors ?? new Dictionary<string, string[]>())
            foreach (var error in errors) ModelState.AddModelError(key, error);
        ModelState.AddModelError("", result.Status switch
        { TrainingOperationStatus.Conflict => "Het moment, de eigenaar of de zaal is intussen niet meer geschikt voor deze wijziging. Herlees bewust vóór een nieuwe poging.",
          TrainingOperationStatus.StorageUnavailable => "Opslaan is tijdelijk niet mogelijk. Probeer later opnieuw.", _ => "Controleer de planning en het maximum." });
    }
}
