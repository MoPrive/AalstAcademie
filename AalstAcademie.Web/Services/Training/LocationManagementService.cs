// Sprint 003: Schrijft uitsluitend actuele adminmutaties onder dezelfde SQLite-write-lock als momenten
// Zaalverlaging beschermt nog niet geëindigde Scheduled-maxima; iedere momentreferentie blokkeert delete.
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Training;

/// <summary>Zaalmutaties zijn actuele adminwrites met dezelfde coördinatie als momentplanning en maximumbeheer.</summary>
public sealed class LocationManagementService(ApplicationDbContext context, TrainingAccessReader access,
    TrainingWriteTransaction transactions, TrainingValueValidation validation, TrainingSchedule schedule)
{
    /// <summary>Een zaalnaam hoeft niet uniek te zijn; de gegenereerde sleutel identificeert de fysieke zaal.</summary>
    public Task<TrainingOperationResult> CreateAsync(string actorUserId, LocationCreateCommand command,
        CancellationToken cancellationToken = default) => transactions.ExecuteAsync(async token =>
    {
        if ((await access.ReadActorAsync(actorUserId, token))?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
        var fields = validation.ValidateLocationFields(command.Fields);
        if (!fields.Succeeded) return new(TrainingOperationStatus.Invalid, Errors: fields.Errors);
        var location = new Location { Name = fields.Fields!.Name, Address = fields.Fields.Address,
            MaximumCapacity = fields.Fields.MaximumCapacity, Version = Guid.NewGuid() };
        context.Locations.Add(location);
        await context.SaveChangesAsync(token);
        return new(TrainingOperationStatus.Success, location.Id);
    }, cancellationToken);

    /// <summary>Geplande maxima beschermen capaciteit tot volledig opgeslagen einde; naam/adres blijven gedeeld actueel.</summary>
    public Task<TrainingOperationResult> UpdateAsync(string actorUserId, LocationUpdateCommand command,
        CancellationToken cancellationToken = default) => transactions.ExecuteAsync(async token =>
    {
        if ((await access.ReadActorAsync(actorUserId, token))?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
        var location = await context.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.Id, token);
        if (location is null) return new(TrainingOperationStatus.NotFound);
        if (!Matches(location.Version, command.ExpectedVersion)) return new(TrainingOperationStatus.Conflict);
        var fields = validation.ValidateLocationFields(command.Fields);
        if (!fields.Succeeded) return new(TrainingOperationStatus.Invalid, Errors: fields.Errors);
        // Deze read gebeurt onder dezelfde write-lock als een momentmaximumverhoging; twee los geldige wijzigingen kunnen de zaalgrens niet samen breken.
        var protectedMoments = await context.TrainingMoments.AsNoTracking()
            .Where(x => x.LocationId == location.Id && x.Status == TrainingMomentStatus.Scheduled &&
                x.MaximumParticipants > fields.Fields!.MaximumCapacity).ToListAsync(token);
        // Einde, niet deelnemertelling/start, bepaalt of een geplande zaalgrens nog beschermd is.
        if (fields.Fields!.MaximumCapacity < location.MaximumCapacity &&
            protectedMoments.Any(x => schedule.IsBeforeStoredEnd(x.Date, x.EndTime))) return new(TrainingOperationStatus.Conflict);
        AttachCurrent(location, command.ExpectedVersion!.Value);
        // De klok opnieuw lezen sluit tijdgrenspassage tijdens validatie expliciet af.
        if (fields.Fields.MaximumCapacity < location.MaximumCapacity &&
            protectedMoments.Any(x => schedule.IsBeforeStoredEnd(x.Date, x.EndTime))) return new(TrainingOperationStatus.Conflict);
        location.Name = fields.Fields.Name; location.Address = fields.Fields.Address;
        location.MaximumCapacity = fields.Fields.MaximumCapacity; location.Version = Guid.NewGuid();
        await context.SaveChangesAsync(token);
        return new(TrainingOperationStatus.Success, location.Id);
    }, cancellationToken);

    /// <summary>Iedere momentreferentie blokkeert delete, ook verleden of Cancelled; FK Restrict blijft vangrail.</summary>
    public Task<TrainingOperationResult> DeleteAsync(string actorUserId, LocationDeleteCommand command,
        CancellationToken cancellationToken = default) => transactions.ExecuteAsync(async token =>
    {
        if ((await access.ReadActorAsync(actorUserId, token))?.IsAdministrator != true) return new(TrainingOperationStatus.Forbidden);
        var location = await context.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.Id, token);
        if (location is null) return new(TrainingOperationStatus.NotFound);
        if (!Matches(location.Version, command.ExpectedVersion)) return new(TrainingOperationStatus.Conflict);
        if (await context.TrainingMoments.AsNoTracking().AnyAsync(x => x.LocationId == location.Id, token))
            return new(TrainingOperationStatus.Conflict);
        AttachCurrent(location, command.ExpectedVersion!.Value);
        context.Locations.Remove(location);
        await context.SaveChangesAsync(token);
        return new(TrainingOperationStatus.Success, location.Id);
    }, cancellationToken);

    // De gelezen zaalversie blijft de SQL-WHERE-voorwaarde; een nieuw token mag een stale formulier niet alsnog geldig maken.
    private void AttachCurrent(Location location, Guid expected)
    {
        var old = context.Locations.Local.FirstOrDefault(x => x.Id == location.Id);
        if (old is not null) context.Entry(old).State = EntityState.Detached;
        context.Locations.Attach(location);
        context.Entry(location).Property(x => x.Version).OriginalValue = expected;
    }
    private static bool Matches(Guid stored, Guid? expected) => expected is not null && expected != Guid.Empty && expected == stored;
}
