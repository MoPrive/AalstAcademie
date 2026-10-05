// Sprint 003: Toetst rechtstreeks de beheerrechten, zaalvalidatie, verlaging onder geplande maxima, historische verwijzingen, stale versies en volledige rollback.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AalstAcademie.Tests.Training.TrainingServiceTestSupport;

namespace AalstAcademie.Tests.Training;

/// <summary>25 varianten bewijzen zaalrechten, geplande maxima, historische referenties en volledige rollback.</summary>
public sealed class LocationManagementServiceTests
{
    [Fact]
    public async Task Create_trims_fields_and_does_not_invent_name_uniqueness()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var identity=await Digest(db,false);
        var first=await Service(db).CreateAsync(admin.Id,new(new("  Fictieve zaal  "," \t",int.MaxValue)));
        var second=await Service(db).CreateAsync(admin.Id,new(new("Fictieve zaal",null,1)));
        Assert.True(first.Succeeded);Assert.True(second.Succeeded);Assert.NotEqual(first.Id,second.Id);
        var stored=await ReadLocation(db,first.Id!.Value);Assert.Equal("Fictieve zaal",stored.Name);Assert.Null(stored.Address);
        Assert.Equal(int.MaxValue,stored.MaximumCapacity);Assert.NotEqual(Guid.Empty,stored.Version);Assert.Equal(identity,await Digest(db,false));
    }

    [Theory] [InlineData("missing")] [InlineData("ineligible")] [InlineData("revoked")] [InlineData("employee")] [InlineData("instructor")]
    public async Task Current_nonadministrators_cannot_mutate_any_room(string group)
    {
        var variants=group=="ineligible"?new[]{"Pending","Refused","blocked","demo-outside"}:
            group=="instructor"?new[]{"internal","external"}:new[]{group};
        foreach(var variant in variants)
        {
            await using var db=await Fixture(demo:variant!="demo-outside");var location=await TrainingTestData.CreateLocationAsync(db.Services);
            var actor=variant=="employee"?await TestAccountData.CreateAsync(db.Services,RequestedAccountType.Employee,AccountApprovalStatus.Approved,roles:[RoleNames.Medewerker]):
                variant is "internal" or "external"?await TrainingTestData.CreateOwnerAsync(db.Services,variant=="external"):
                await TrainingTestData.CreateAdministratorAsync(db.Services);
            await DenyActor(db,actor.Id,variant);var id=variant=="missing"?"ontbrekend":actor.Id;var before=await Digest(db);
            Assert.Equal(TrainingOperationStatus.Forbidden,(await Service(db).CreateAsync(id,new(new("Nieuwe zaal",null,24)))).Status);
            Assert.Equal(TrainingOperationStatus.Forbidden,(await Service(db).UpdateAsync(id,new(location.Id,location.Version,new("Nieuw",null,24)))).Status);
            Assert.Equal(TrainingOperationStatus.Forbidden,(await Service(db).DeleteAsync(id,new(location.Id,location.Version))).Status);
            Assert.Equal(before,await Digest(db));
        }
    }

    [Fact]
    public async Task Historical_room_name_and_address_remain_shared_without_moment_mutation()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training,date:new(2025,1,15));
        var before=await ReadMoment(db,moment.Id);
        Assert.True((await Service(db).UpdateAsync(admin.Id,new(moment.LocationId,moment.Location.Version,new("Actuele zaal","Actueel adres",24)))).Succeeded);
        var after=await ReadMoment(db,moment.Id);Assert.Equal("Actuele zaal",after.Location.Name);Assert.Equal("Actueel adres",after.Location.Address);
        Assert.Equal(before.Version,after.Version);Assert.Equal(before.MaximumParticipants,after.MaximumParticipants);Assert.Equal(before.Date,after.Date);
    }

    [Fact]
    public async Task Increasing_capacity_never_increases_planned_moment_maximum()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);
        Assert.True((await Service(db).UpdateAsync(admin.Id,new(moment.LocationId,moment.Location.Version,new("Fictieve zaal",null,30)))).Succeeded);
        var after=await ReadMoment(db,moment.Id);Assert.Equal(12,after.MaximumParticipants);Assert.Equal(moment.Version,after.Version);
        Assert.Equal(30,after.Location.MaximumCapacity);
    }

    [Theory] [InlineData(false,-1)] [InlineData(false,0)] [InlineData(false,1)]
    [InlineData(true,-1)] [InlineData(true,0)] [InlineData(true,1)]
    public async Task Planned_maximum_is_protected_until_full_stored_end_only_if_scheduled(bool cancelled,int relativeTick)
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training,end:new TimeOnly(16,0).Add(TimeSpan.FromTicks(123)),
            status:cancelled?TrainingMomentStatus.Cancelled:TrainingMomentStatus.Scheduled);
        db.Services.GetRequiredService<FixedTimeProvider>().UtcNow=EndUtc(db,moment).AddTicks(relativeTick);
        var before=await Digest(db);
        var result=await Service(db).UpdateAsync(admin.Id,new(moment.LocationId,moment.Location.Version,new("Fictieve zaal",null,8)));
        var blocked=!cancelled&&relativeTick<0;Assert.Equal(blocked?TrainingOperationStatus.Conflict:TrainingOperationStatus.Success,result.Status);
        if(blocked)Assert.Equal(before,await Digest(db));
        var after=await ReadMoment(db,moment.Id);Assert.Equal(12,after.MaximumParticipants);Assert.Equal(moment.Version,after.Version);
        Assert.Equal(blocked?24:8,after.Location.MaximumCapacity);
    }

    [Theory] [InlineData("future")] [InlineData("past")] [InlineData("cancelled")]
    public async Task Any_moment_reference_prevents_room_deletion(string variant)
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training,date:variant=="past"?new DateOnly(2025,1,15):null,
            status:variant=="cancelled"?TrainingMomentStatus.Cancelled:TrainingMomentStatus.Scheduled);
        var before=await Digest(db);Assert.Equal(TrainingOperationStatus.Conflict,
            (await Service(db).DeleteAsync(admin.Id,new(moment.LocationId,moment.Location.Version))).Status);
        Assert.Equal(before,await Digest(db));
    }

    [Theory] [InlineData("missing")] [InlineData("empty")] [InlineData("stale")]
    public async Task Original_room_version_is_required_for_update_and_delete(string variant)
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var room=await TrainingTestData.CreateLocationAsync(db.Services);var before=await Digest(db);var version=Version(variant);
        Assert.Equal(TrainingOperationStatus.Conflict,(await Service(db).UpdateAsync(admin.Id,new(room.Id,version,new("Wijziging",null,24)))).Status);
        Assert.Equal(TrainingOperationStatus.Conflict,(await Service(db).DeleteAsync(admin.Id,new(room.Id,version))).Status);
        Assert.Equal(before,await Digest(db));
    }

    [Theory] [InlineData("location-update")] [InlineData("location-delete")]
    public async Task Late_room_sql_failure_restores_row_and_version_and_allows_clean_retry(string point)
    {
        var fault=new TrainingFailureInterceptor();await using var db=await Fixture(fault);var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var room=await TrainingTestData.CreateLocationAsync(db.Services);var before=await Digest(db);
        Task<TrainingOperationResult> Act()=>point=="location-update"?Service(db).UpdateAsync(admin.Id,new(room.Id,room.Version,new("Nieuwe naam",null,30))):
            Service(db).DeleteAsync(admin.Id,new(room.Id,room.Version));
        fault.Arm(point);Assert.Equal(TrainingOperationStatus.StorageUnavailable,(await Act()).Status);
        Assert.True(fault.Triggered);Assert.True(fault.SawSqlWrite);Assert.Equal(before,await Digest(db));
        fault.Disarm();Assert.True((await Act()).Succeeded);
    }

    [Fact]
    public async Task Unused_room_read_is_not_a_delete_but_explicit_versioned_command_removes_it()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var room=await TrainingTestData.CreateLocationAsync(db.Services);var before=await Digest(db);
        Assert.True((await db.Services.GetRequiredService<LocationManagementQueries>().GetDetailsAsync(admin.Id,room.Id)).Value!.CanDelete);
        Assert.Equal(before,await Digest(db));Assert.True((await Service(db).DeleteAsync(admin.Id,new(room.Id,room.Version))).Succeeded);
        await using var read=db.CreateScope();Assert.False(await read.ServiceProvider.GetRequiredService<ApplicationDbContext>().Locations.AnyAsync(x=>x.Id==room.Id));
    }

    [Fact]
    public async Task Missing_room_update_and_delete_do_not_leak_or_mutate()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);var before=await Digest(db);
        Assert.Equal(TrainingOperationStatus.NotFound,(await Service(db).UpdateAsync(admin.Id,new(int.MaxValue,Guid.NewGuid(),new("Geldig",null,24)))).Status);
        Assert.Equal(TrainingOperationStatus.NotFound,(await Service(db).DeleteAsync(admin.Id,new(int.MaxValue,Guid.NewGuid()))).Status);
        Assert.Equal(before,await Digest(db));
    }

    [Fact]
    public async Task Invalid_capacity_or_fields_are_rejected_before_any_room_write()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var room=await TrainingTestData.CreateLocationAsync(db.Services);var before=await Digest(db);
        foreach(var fields in new LocationFields[]{new("Geldig",null,0),new("Geldig",null,null),new(" ",null,24),new("Geldig",new string('a',501),24)})
        {
            Assert.Equal(TrainingOperationStatus.Invalid,(await Service(db).CreateAsync(admin.Id,new(fields))).Status);
            Assert.Equal(TrainingOperationStatus.Invalid,(await Service(db).UpdateAsync(admin.Id,new(room.Id,room.Version,fields))).Status);
            Assert.Equal(before,await Digest(db));
        }
    }
    private static LocationManagementService Service(FileSqliteTestDatabase db)=>db.Services.GetRequiredService<LocationManagementService>();
}
