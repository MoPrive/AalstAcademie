// Sprint 003: Controleert huidige actor/eigenaar, opgeslagen start, historie, bezetting, zaal en overlap vóór een atomische momentwrite
// De gezamenlijke lock voorkomt races tussen verschillende zaal- en momentrijen.
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Models.Domain;
using Microsoft.EntityFrameworkCore;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Web.Services.Training;

/// <summary>Momentschrijven coördineert actuele scope, zaal, overlap, historie, klok en versie in één writegrens.</summary>
public sealed class TrainingMomentManagementService(ApplicationDbContext context, TrainingAccessReader access,
    TrainingWriteTransaction transactions, TrainingValueValidation validation, TrainingSchedule schedule,
    TrainingFoundationQueries foundation, WaitlistPromotionService promotion)
{
    /// <summary>D76 controleert de vaste opgeslagen eigenaar opnieuw; een actor kan geen eigenaar overdragen.</summary>
    public Task<TrainingOperationResult> CreateAsync(string actorUserId, TrainingMomentCreateCommand command,
        CancellationToken cancellationToken = default) => transactions.ExecuteAsync(async token =>
    {
        var actor = await access.ReadActorAsync(actorUserId, token);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var training = await ReadTrainingAsync(actor, command.TrainingId, token);
        if (training is null) return new(TrainingOperationStatus.NotFound);
        if (await access.ReadEligibleOwnerAsync(training.InstructorUserId, token) is null) return new(TrainingOperationStatus.Conflict);
        var fields = validation.ValidateMomentFields(command.Fields);
        if (!fields.Succeeded) return new(TrainingOperationStatus.Invalid, Errors: fields.Errors);
        var value = fields.Fields!;
        var location = await context.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == value.LocationId, token);
        if (location is null || value.MaximumParticipants > location.MaximumCapacity) return new(TrainingOperationStatus.Conflict);
        if (await OverlapsAsync(value.Date, value.StartTime, value.EndTime, value.LocationId, null, token))
            return new(TrainingOperationStatus.Conflict);
        var moment = new TrainingMoment { TrainingId = training.Id, LocationId = location.Id, Date = value.Date,
            StartTime = value.StartTime, EndTime = value.EndTime, MaximumParticipants = value.MaximumParticipants,
            Status = TrainingMomentStatus.Scheduled, Version = Guid.NewGuid() };
        context.TrainingMoments.Add(moment);
        await context.SaveChangesAsync(token);
        var fill = await promotion.FillMomentInsideTransactionAsync(moment.Id, token);
        return fill.Succeeded ? new(TrainingOperationStatus.Success, moment.Id) : fill;
    }, cancellationToken);

    /// <summary>Planningedit behoudt maximum; iedere registratiehistorie blokkeert deze command, ook zonder veldverschil.</summary>
    public Task<TrainingOperationResult> UpdateAsync(string actorUserId, TrainingMomentUpdateCommand command,
        CancellationToken cancellationToken = default) => transactions.ExecuteAsync(async token =>
    {
        var actor = await access.ReadActorAsync(actorUserId, token);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var moment = await ReadMomentAsync(actor, command.Id, token);
        if (moment is null) return new(TrainingOperationStatus.NotFound);
        if (!Matches(moment.Version, command.ExpectedVersion) || !schedule.CanEdit(moment)) return new(TrainingOperationStatus.Conflict);
        var fields = validation.ValidateMomentFields(new(command.Fields.Date, command.Fields.StartTime,
            command.Fields.EndTime, command.Fields.LocationId, moment.MaximumParticipants));
        if (!fields.Succeeded) return new(TrainingOperationStatus.Invalid, Errors: fields.Errors);
        var value = fields.Fields!;
        if (await foundation.HasMomentRegistrationsAsync(moment.Id, token)) return new(TrainingOperationStatus.Conflict);
        var location = await context.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == value.LocationId, token);
        if (location is null || moment.MaximumParticipants > location.MaximumCapacity) return new(TrainingOperationStatus.Conflict);
        if (await OverlapsAsync(value.Date, value.StartTime, value.EndTime, value.LocationId, moment.Id, token))
            return new(TrainingOperationStatus.Conflict);
        // De geposte toekomstige datum mag de opgeslagen-startgrens nooit vervangen.
        if (!schedule.CanEdit(moment)) return new(TrainingOperationStatus.Conflict);
        AttachCurrent(moment, command.ExpectedVersion!.Value);
        moment.Date = value.Date; moment.StartTime = value.StartTime; moment.EndTime = value.EndTime; moment.LocationId = value.LocationId;
        moment.Version = Guid.NewGuid();
        await context.SaveChangesAsync(token);
        return new(TrainingOperationStatus.Success, moment.Id);
    }, cancellationToken);

    /// <summary>De echte Requested+Confirmed-bezetting en huidige zaalcap begrenzen alleen het momentmaximum.</summary>
    public Task<TrainingOperationResult> ChangeCapacityAsync(string actorUserId, TrainingMomentCapacityCommand command,
        CancellationToken cancellationToken = default) => transactions.ExecuteAsync(async token =>
    {
        var actor = await access.ReadActorAsync(actorUserId, token);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var moment = await ReadMomentAsync(actor, command.Id, token);
        if (moment is null) return new(TrainingOperationStatus.NotFound);
        if (!Matches(moment.Version, command.ExpectedVersion) || !schedule.CanEdit(moment)) return new(TrainingOperationStatus.Conflict);
        if (command.MaximumParticipants is null or <= 0)
            return new(TrainingOperationStatus.Invalid, Errors: new Dictionary<string, string[]> { ["MaximumParticipants"] = ["Vul een positief geheel maximum in."] });
        var location = await context.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == moment.LocationId, token);
        // Een maximum is minstens één en minstens de echte Requested+Confirmed-bezetting; geweigerde/geannuleerde rijen tellen hier niet.
        var minimum = Math.Max(1, await foundation.CountOccupiedAsync(moment.Id, token));
        if (location is null || command.MaximumParticipants < minimum || command.MaximumParticipants > location.MaximumCapacity)
            return new(TrainingOperationStatus.Conflict);
        if (!schedule.CanEdit(moment)) return new(TrainingOperationStatus.Conflict);
        AttachCurrent(moment, command.ExpectedVersion!.Value);
        var raised = command.MaximumParticipants.Value > moment.MaximumParticipants;
        moment.MaximumParticipants = command.MaximumParticipants.Value; moment.Version = Guid.NewGuid();
        await context.SaveChangesAsync(token);
        if (raised)
        {
            var fill = await promotion.FillMomentInsideTransactionAsync(moment.Id, token);
            if (!fill.Succeeded) return fill;
        }
        return new(schedule.CanEdit(moment) ? TrainingOperationStatus.Success : TrainingOperationStatus.Conflict, moment.Id);
    }, cancellationToken);

    // Momentbeheer erft de scope van de vaste opleiding-eigenaar; het child-ID is geen afzonderlijke toegangspoort.
    private async Task<TrainingEntity?> ReadTrainingAsync(TrainingActor actor, int id, CancellationToken token)
    {
        var training = await context.Trainings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return training is not null && await access.CanReadTrainingAsync(actor, training, token) ? training : null;
    }
    // Lees de parent mee om ook bij directe serviceaanroepen de eigenaarsscope te bewaken.
    private async Task<TrainingMoment?> ReadMomentAsync(TrainingActor actor, int id, CancellationToken token)
    {
        var moment = await context.TrainingMoments.AsNoTracking().Include(x => x.Training).SingleOrDefaultAsync(x => x.Id == id, token);
        return moment is not null && await access.CanReadTrainingAsync(actor, moment.Training, token) ? moment : null;
    }
    // Halve open intervallen: einde gelijk aan volgend begin is toegestaan; alleen Scheduled blokkeert dezelfde zaal op dezelfde dag.
    private Task<bool> OverlapsAsync(DateOnly date, TimeOnly start, TimeOnly end, int locationId, int? excludedId, CancellationToken token) =>
        context.TrainingMoments.AsNoTracking().AnyAsync(x => x.LocationId == locationId && x.Date == date &&
            x.Status == TrainingMomentStatus.Scheduled && (excludedId == null || x.Id != excludedId) &&
            start < x.EndTime && x.StartTime < end, token);
    // Attach uitsluitend de momentrij met de oorspronkelijke CAS-versie, zodat parent- en zaalnavigaties geen extra writes veroorzaken.
    private void AttachCurrent(TrainingMoment moment, Guid expected)
    {
        var old = context.TrainingMoments.Local.FirstOrDefault(x => x.Id == moment.Id);
        if (old is not null) context.Entry(old).State = EntityState.Detached;
        // Navigatie niet attachen: de command bezit alleen de momentwrite, geen Training-mutatie.
        moment.Training = null!; moment.Location = null!;
        context.TrainingMoments.Attach(moment);
        context.Entry(moment).Property(x => x.Version).OriginalValue = expected;
    }
    private static bool Matches(Guid stored, Guid? expected) => expected is not null && expected != Guid.Empty && expected == stored;
}
