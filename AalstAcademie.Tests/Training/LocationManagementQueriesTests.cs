// Sprint 003: Controleert adminscope, actuele gedeelde zaallabels, beschermde geplande maxima en de verwijderguard op iedere momentreferentie.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AalstAcademie.Tests.Training.TrainingServiceTestSupport;

namespace AalstAcademie.Tests.Training;

/// <summary>Drie querygroepen bewijzen adminscope, actuele zaalgegevens en echte historische deleteguards.</summary>
public sealed class LocationManagementQueriesTests
{
    [Fact]
    public async Task Only_current_administrator_reads_room_management()
    {
        await using var db=await Fixture();var room=await TrainingTestData.CreateLocationAsync(db.Services);
        var actor=await TrainingTestData.CreateOwnerAsync(db.Services);var before=await Digest(db);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Query(db).GetListAsync(actor.Id)).Status);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Query(db).GetDetailsAsync(actor.Id,room.Id)).Status);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Query(db).GetDetailsAsync("ontbrekend",room.Id)).Status);
        Assert.Equal(before,await Digest(db));
        var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        Assert.True((await Query(db).GetListAsync(admin.Id)).Succeeded);
        await DenyActor(db,admin.Id,"revoked");var denied=await Digest(db);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Query(db).GetDetailsAsync(admin.Id,room.Id)).Status);Assert.Equal(denied,await Digest(db));
    }

    [Fact]
    public async Task Current_name_address_capacity_and_planned_minimum_are_independent_reads()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training,maximumParticipants:16);
        Assert.True((await db.Services.GetRequiredService<LocationManagementService>().UpdateAsync(admin.Id,
            new(moment.LocationId,moment.Location.Version,new("Huidige naam","Huidig adres",30)))).Succeeded);
        var before=await Digest(db);var row=(await Query(db).GetDetailsAsync(admin.Id,moment.LocationId)).Value!;
        Assert.Equal("Huidige naam",row.Name);Assert.Equal("Huidig adres",row.Address);Assert.Equal(30,row.MaximumCapacity);
        Assert.Equal(16,row.MinimumCapacity);Assert.True(row.HasMoments);Assert.False(row.CanDelete);Assert.Equal(before,await Digest(db));
    }

    [Fact]
    public async Task Any_scheduled_past_or_cancelled_reference_blocks_delete_flag_but_unused_room_does_not()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));
        var unused=await TrainingTestData.CreateLocationAsync(db.Services,"Ongebruikte zaal");
        var future=await TrainingTestData.CreateMomentAsync(db.Services,training);
        var past=await TrainingTestData.CreateMomentAsync(db.Services,training,date:new(2025,1,15));
        var cancelled=await TrainingTestData.CreateMomentAsync(db.Services,training,status:TrainingMomentStatus.Cancelled);
        var before=await Digest(db);
        foreach(var id in new[]{future.LocationId,past.LocationId,cancelled.LocationId})
            Assert.False((await Query(db).GetDetailsAsync(admin.Id,id)).Value!.CanDelete);
        Assert.True((await Query(db).GetDetailsAsync(admin.Id,unused.Id)).Value!.CanDelete);
        Assert.Equal(TrainingOperationStatus.NotFound,(await Query(db).GetDetailsAsync(admin.Id,int.MaxValue)).Status);
        Assert.Equal(before,await Digest(db));
    }
    private static LocationManagementQueries Query(FileSqliteTestDatabase db)=>db.Services.GetRequiredService<LocationManagementQueries>();
}
