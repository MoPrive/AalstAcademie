// Sprint 003: Leest scoped momenten en zalen op huidige capaciteit en Scheduled-overlap
// Een beschikbaarheidsresultaat reserveert niets; opslaan controleert dezelfde guards opnieuw.
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Training;

/// <summary>Scoped momentreads en zaalbeschikbaarheid; een gelezen vrije zaal is geen reservering.</summary>
public sealed class TrainingMomentManagementQueries(ApplicationDbContext context, TrainingAccessReader access,
    TrainingSchedule schedule, TrainingFoundationQueries foundation)
{
    // Controleer eerst opleidingsscope, daarna pas de children; stabiele datum/tijd/ID-volgorde maakt de lijst voorspelbaar.
    public async Task<TrainingQueryResult<IReadOnlyList<TrainingMomentRowReadModel>>> GetListAsync(string actorUserId, int trainingId,
        CancellationToken cancellationToken = default)
    {
        var scope = await ScopeAsync(actorUserId, trainingId, cancellationToken);
        if (scope != TrainingOperationStatus.Success) return new(scope);
        return new(TrainingOperationStatus.Success, await Rows(context.TrainingMoments.AsNoTracking().Where(x => x.TrainingId == trainingId)
            .OrderBy(x => x.Date).ThenBy(x => x.StartTime).ThenBy(x => x.Id)).ToListAsync(cancellationToken));
    }

    /// <summary>Registratiestatussen komen alleen als noodzakelijke telling/guardflag terug, nooit als deelnemerslijst.</summary>
    public async Task<TrainingQueryResult<TrainingMomentDetailsReadModel>> GetDetailsAsync(string actorUserId, int id,
        CancellationToken cancellationToken = default)
    {
        var actor = await access.ReadActorAsync(actorUserId, cancellationToken);
        if (actor is null) return new(TrainingOperationStatus.Forbidden);
        var moment = await context.TrainingMoments.AsNoTracking().Include(x => x.Training).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (moment is null || !await access.CanReadTrainingAsync(actor, moment.Training, cancellationToken)) return new(TrainingOperationStatus.NotFound);
        var row = await Rows(context.TrainingMoments.AsNoTracking().Where(x => x.Id == id)).SingleAsync(cancellationToken);
        var history = await foundation.HasMomentRegistrationsAsync(id, cancellationToken);
        var editable = schedule.CanEdit(moment);
        return new(TrainingOperationStatus.Success, new(row, editable && !history, editable, history,
            await foundation.CountOccupiedAsync(id, cancellationToken)) { CanCancel = actor.IsAdministrator && editable });
    }

    /// <summary>Ownscope en een eventueel eigen moment worden vóór datumfouten getoetst; excluded ID is geen vrije bypass.</summary>
    public async Task<TrainingQueryResult<TrainingAvailabilityReadModel>> GetAvailabilityAsync(string actorUserId, int trainingId,
        DateOnly? date, TimeOnly? start, TimeOnly? end, int? maximumParticipants = null, int? excludeMomentId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await ScopeAsync(actorUserId, trainingId, cancellationToken);
        if (scope != TrainingOperationStatus.Success) return new(scope);
        if (excludeMomentId is int excluded && !await context.TrainingMoments.AsNoTracking()
                .AnyAsync(x => x.Id == excluded && x.TrainingId == trainingId, cancellationToken))
            return new(TrainingOperationStatus.NotFound);
        var timing = schedule.Validate(date, start, end);
        if (!timing.Succeeded) return new(TrainingOperationStatus.Invalid, Errors: timing.Errors);
        if (maximumParticipants is <= 0) return new(TrainingOperationStatus.Invalid, Errors:
            new Dictionary<string, string[]> { ["MaximumParticipants"] = ["Vul een positief geheel maximum in."] });
        var value = timing.Value!;
        // Een conflict bestaat ook met een moment van een andere opleiding. Alleen de bewust gecontroleerde eigen editrij wordt uitgesloten.
        var occupiedRooms = await context.TrainingMoments.AsNoTracking().Where(x => x.Date == value.Date &&
            x.Status == TrainingMomentStatus.Scheduled && (excludeMomentId == null || x.Id != excludeMomentId) &&
            value.StartTime < x.EndTime && x.StartTime < value.EndTime).Select(x => x.LocationId).Distinct().ToListAsync(cancellationToken);
        var locations = await context.Locations.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        var rooms = locations.Select(x => new TrainingAvailabilityRoomReadModel(x.Id, x.Name, x.Address, x.MaximumCapacity,
            !occupiedRooms.Contains(x.Id) && (maximumParticipants is null || maximumParticipants <= x.MaximumCapacity))).ToArray();
        return new(TrainingOperationStatus.Success, new(value.Date, value.StartTime, value.EndTime, rooms));
    }

    public async Task<TrainingQueryResult<TrainingMomentCancelReadModel>> GetCancelAsync(string actorId, int id, CancellationToken ct = default)
    {
        if ((await access.ReadActorAsync(actorId, ct))?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
        var m = await context.TrainingMoments.AsNoTracking().Include(x => x.Training).Include(x => x.Location)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return new(TrainingOperationStatus.NotFound);
        return new(TrainingOperationStatus.Success, new(m.Id, m.TrainingId, m.Training.Title, m.Location.Name, m.Date, m.StartTime,
            await context.Registrations.CountAsync(x => x.TrainingMomentId == id && x.Status == RegistrationStatus.Requested, ct),
            await context.Registrations.CountAsync(x => x.TrainingMomentId == id && x.Status == RegistrationStatus.Confirmed, ct), m.Version, schedule.CanEdit(m)));
    }

    // Ook een beschikbaarheidsquery is privé: actorrechten en opleidingseigendom gaan vóór planningvalidatie.
    private async Task<TrainingOperationStatus> ScopeAsync(string actorUserId, int trainingId, CancellationToken token)
    {
        var actor = await access.ReadActorAsync(actorUserId, token);
        if (actor is null) return TrainingOperationStatus.Forbidden;
        var training = await context.Trainings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == trainingId, token);
        return training is not null && await access.CanReadTrainingAsync(actor, training, token)
            ? TrainingOperationStatus.Success : TrainingOperationStatus.NotFound;
    }

    // Lees alleen beheergegevens en gedeelde labels; registratie-inhoud en deelnemersgegevens blijven buiten deze projectie.
    private static IQueryable<TrainingMomentRowReadModel> Rows(IQueryable<TrainingMoment> query) => query.Select(x =>
        new TrainingMomentRowReadModel(x.Id, x.TrainingId, x.Training.Title, x.LocationId, x.Location.Name,
            x.Location.MaximumCapacity, x.Date, x.StartTime, x.EndTime, x.MaximumParticipants, x.Status, x.Version));
}
