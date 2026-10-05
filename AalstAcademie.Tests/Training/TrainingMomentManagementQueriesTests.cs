// Sprint 003: Controleert scoped momentgegevens en zaalbeschikbaarheid
// Een vrije zaal in een leesresultaat is geen boeking en levert geen namen of motivaties van deelnemers op.
using System.Text.Json;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Training;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AalstAcademie.Tests.Training.TrainingServiceTestSupport;

namespace AalstAcademie.Tests.Training;

/// <summary>Vijf querygroepen bewijzen resource-scope, actuele uitvoeringgegevens en beschikbaarheid zonder deelnemersdata.</summary>
public sealed class TrainingMomentManagementQueriesTests
{
    [Fact]
    public async Task Owner_and_administrator_read_their_allowed_moment_lists_and_details()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);
        var before=await Digest(db);
        foreach(var actor in new[]{owner,admin})
        {
            Assert.Equal(moment.Id,Assert.Single((await Query(db).GetListAsync(actor.Id,training.Id)).Value!).Id);
            var details=(await Query(db).GetDetailsAsync(actor.Id,moment.Id)).Value!;
            Assert.True(details.CanEditPlanning);Assert.True(details.CanEditCapacity);Assert.False(details.HasRegistrations);Assert.Equal(0,details.OccupiedCount);
        }
        Assert.Equal(before,await Digest(db));
    }

    [Fact]
    public async Task Foreign_missing_and_foreign_exclusion_are_notfound_before_malformed_planning()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var own=await TrainingTestData.CreateMomentAsync(db.Services,training);
        var foreignTraining=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));
        var foreign=await TrainingTestData.CreateMomentAsync(db.Services,foreignTraining);var before=await Digest(db);
        Assert.Equal(TrainingOperationStatus.NotFound,(await Query(db).GetListAsync(owner.Id,foreignTraining.Id)).Status);
        foreach(var id in new[]{foreign.Id,int.MaxValue})Assert.Equal(TrainingOperationStatus.NotFound,(await Query(db).GetDetailsAsync(owner.Id,id)).Status);
        Assert.Equal(TrainingOperationStatus.NotFound,(await Query(db).GetAvailabilityAsync(owner.Id,training.Id,null,null,null,excludeMomentId:foreign.Id)).Status);
        Assert.Equal(TrainingOperationStatus.NotFound,(await Query(db).GetAvailabilityAsync(owner.Id,foreignTraining.Id,null,null,null)).Status);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Query(db).GetDetailsAsync("ontbrekend",own.Id)).Status);
        Assert.True((await Query(db).GetAvailabilityAsync(owner.Id,training.Id,own.Date,own.StartTime,own.EndTime,excludeMomentId:own.Id)).Value!.Rooms.Single(x=>x.Id==own.LocationId).IsAvailable);
        Assert.Equal(before,await Digest(db));
    }

    [Fact]
    public async Task Current_metadata_room_tick_precision_status_and_real_occupied_aggregate_are_visible()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training,start:new TimeOnly(14,0).Add(TimeSpan.FromTicks(1)),end:new TimeOnly(16,0).Add(TimeSpan.FromTicks(7)));
        await TrainingTestData.CreateRegistrationAsync(db.Services,moment,RegistrationStatus.Requested);
        await TrainingTestData.CreateRegistrationAsync(db.Services,moment,RegistrationStatus.Refused);
        Assert.True((await db.Services.GetRequiredService<TrainingManagementService>().UpdateAsync(owner.Id,
            new(training.Id,training.Version,Fields(training) with {Title="Huidige definitie"}))).Succeeded);
        Assert.True((await db.Services.GetRequiredService<LocationManagementService>().UpdateAsync(admin.Id,
            new(moment.LocationId,moment.Location.Version,new("Huidige zaal",null,30)))).Succeeded);
        var before=await Digest(db);var details=(await Query(db).GetDetailsAsync(owner.Id,moment.Id)).Value!;
        Assert.Equal("Huidige definitie",details.Moment.TrainingTitle);Assert.Equal("Huidige zaal",details.Moment.LocationName);Assert.Equal(30,details.Moment.LocationMaximumCapacity);
        Assert.Equal(moment.StartTime,details.Moment.StartTime);Assert.Equal(moment.EndTime,details.Moment.EndTime);Assert.Equal(TrainingMomentStatus.Scheduled,details.Moment.Status);
        Assert.Equal(1,details.OccupiedCount);Assert.True(details.HasRegistrations);Assert.False(details.CanEditPlanning);Assert.True(details.CanEditCapacity);
        Assert.Equal(before,await Digest(db));
        // Cancelled is uitsluitend een opslagfixture: deze proef levert geen annuleringsroute.
        db.Context.ChangeTracker.Clear();var stored=await db.Context.TrainingMoments.SingleAsync(x=>x.Id==moment.Id);stored.Status=TrainingMomentStatus.Cancelled;await db.Context.SaveChangesAsync();
        var cancelled=(await Query(db).GetDetailsAsync(owner.Id,moment.Id)).Value!;
        Assert.Equal(TrainingMomentStatus.Cancelled,cancelled.Moment.Status);Assert.False(cancelled.CanEditPlanning);Assert.False(cancelled.CanEditCapacity);
    }

    [Fact]
    public async Task Availability_handles_adjacent_cancelled_and_stale_reads_without_reserving_rooms()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var occupied=await TrainingTestData.CreateMomentAsync(db.Services,training);
        var cancelled=await TrainingTestData.CreateMomentAsync(db.Services,training,status:TrainingMomentStatus.Cancelled);
        var before=await Digest(db);
        var overlap=(await Query(db).GetAvailabilityAsync(owner.Id,training.Id,occupied.Date,occupied.StartTime,occupied.EndTime)).Value!;
        Assert.False(overlap.Rooms.Single(x=>x.Id==occupied.LocationId).IsAvailable);Assert.True(overlap.Rooms.Single(x=>x.Id==cancelled.LocationId).IsAvailable);
        var adjacent=(await Query(db).GetAvailabilityAsync(owner.Id,training.Id,occupied.Date,new(16,0),new(18,0),12)).Value!;
        Assert.True(adjacent.Rooms.Single(x=>x.Id==occupied.LocationId).IsAvailable);Assert.Equal(before,await Digest(db));
        Assert.True((await db.Services.GetRequiredService<TrainingMomentManagementService>().CreateAsync(owner.Id,
            new(training.Id,new(occupied.Date,new(16,0),new(18,0),occupied.LocationId,12)))).Succeeded);
        Assert.True(adjacent.Rooms.Single(x=>x.Id==occupied.LocationId).IsAvailable);
        var afterBooking=await Digest(db);
        Assert.False((await Query(db).GetAvailabilityAsync(owner.Id,training.Id,occupied.Date,new(16,0),new(18,0),12)).Value!.Rooms.Single(x=>x.Id==occupied.LocationId).IsAvailable);
        Assert.Equal(afterBooking,await Digest(db));
    }

    [Fact]
    public async Task Availability_contract_and_serialized_values_have_no_participant_or_contact_data()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);
        var registration=await TrainingTestData.CreateRegistrationAsync(db.Services,moment);
        foreach(var type in new[]{typeof(TrainingAvailabilityReadModel),typeof(TrainingAvailabilityRoomReadModel)})
            Assert.DoesNotContain(type.GetProperties(),p=>new[]{"Participant","Registration","Occupied","Email","Phone","Motivation","User","Contact"}
                .Any(word=>p.Name.Contains(word,StringComparison.OrdinalIgnoreCase)));
        var before=await Digest(db);var availability=(await Query(db).GetAvailabilityAsync(owner.Id,training.Id,moment.Date,moment.StartTime,moment.EndTime)).Value!;
        var json=JsonSerializer.Serialize(availability);Assert.DoesNotContain(registration.ApplicationUserId,json);Assert.DoesNotContain(registration.Motivation!,json);
        Assert.Equal(before,await Digest(db));
    }
    private static TrainingMomentManagementQueries Query(FileSqliteTestDatabase db)=>db.Services.GetRequiredService<TrainingMomentManagementQueries>();
}
