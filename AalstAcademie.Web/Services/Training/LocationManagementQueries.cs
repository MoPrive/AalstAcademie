// Sprint 003: Leest uitsluitend voor een actuele beheerder zaalgegevens, geplande maxima vóór einde en iedere momentreferentie
// De flags ondersteunen formulieren; writes hercontroleren alles.
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Training;

/// <summary>Beheerders lezen huidige gedeelde zaalgegevens; referenties en minimum komen uit echte moments.</summary>
public sealed class LocationManagementQueries(ApplicationDbContext context, TrainingAccessReader access, TrainingSchedule schedule)
{
    public async Task<TrainingQueryResult<IReadOnlyList<LocationReadModel>>> GetListAsync(string actorUserId,
        CancellationToken cancellationToken = default)
    {
        if ((await access.ReadActorAsync(actorUserId, cancellationToken))?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
        var locations = await context.Locations.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        var rows = new List<LocationReadModel>();
        foreach (var location in locations) rows.Add(await RowAsync(location, cancellationToken));
        return new(TrainingOperationStatus.Success, rows);
    }

    public async Task<TrainingQueryResult<LocationReadModel>> GetDetailsAsync(string actorUserId, int id,
        CancellationToken cancellationToken = default)
    {
        if ((await access.ReadActorAsync(actorUserId, cancellationToken))?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
        var location = await context.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return location is null ? new(TrainingOperationStatus.NotFound)
            : new(TrainingOperationStatus.Success, await RowAsync(location, cancellationToken));
    }

    // Het referentieflag gebruikt alle momenten; de capaciteitsgrens gebruikt alleen Scheduled-momenten vóór hun opgeslagen einde.
    private async Task<LocationReadModel> RowAsync(Location location, CancellationToken token)
    {
        var moments = await context.TrainingMoments.AsNoTracking().Where(x => x.LocationId == location.Id).ToListAsync(token);
        var minimum = moments.Where(x => x.Status == TrainingMomentStatus.Scheduled && schedule.IsBeforeStoredEnd(x.Date, x.EndTime))
            .Select(x => x.MaximumParticipants).DefaultIfEmpty(1).Max();
        return new(location.Id, location.Name, location.Address, location.MaximumCapacity, location.Version, moments.Count != 0, minimum);
    }
}
