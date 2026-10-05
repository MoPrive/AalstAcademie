// Sprint 003: Toetst overlap, planning tegenover eerste registratie, zaalverwijzingen, bezetting, verstreken start en deelnemersmaximum tegenover zaalverlaging onder echte SQLite-locks.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Training;

/// <summary>Tien provider-races bewaken planning, referenties, bezetting, zaalgrens en klok na een echte lockwacht.</summary>
[Collection("Revision02 provider races")]
public class TrainingMomentConcurrencyTests
{
    // Verschillende nieuwe momentrijen hebben geen gezamenlijke CAS-versie; de SQLite-write-lock moet de gedeelde zaal toch beschermen.
    [Fact] public async Task Overlapping_booking_has_one_winner()
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync(moment:false);
            Task<TrainingOperationResult> Book(IServiceProvider sp,int start,int end)=>sp.GetRequiredService<TrainingMomentManagementService>().CreateAsync(s.Owner.Id,
                new(s.Training.Id,new(new DateOnly(2026,12,15),new TimeOnly(start,0),new TimeOnly(end,0),s.Room.Id,10)));
            var results=await s.Race(sp=>Book(sp,reversed?15:14,reversed?17:16),sp=>Book(sp,reversed?14:15,reversed?16:17));
            RaceFixture.OneSuccess(results);
            await s.Read(async sp=>{var row=await sp.GetRequiredService<ApplicationDbContext>().TrainingMoments.AsNoTracking().SingleAsync();
                Assert.Equal(new TimeOnly(reversed?15:14,0),row.StartTime);Assert.Equal(s.Training.Id,row.TrainingId);Assert.Equal(10,row.MaximumParticipants);});
        }
    }
    // De overlapregel moet twee aansluitende intervallen toelaten; een te strenge grens zou onnodig beschikbare tijd blokkeren.
    [Fact] public async Task Adjacent_booking_both_commit()
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync(moment:false);
            Task<TrainingOperationResult> Book(IServiceProvider sp,int start)=>sp.GetRequiredService<TrainingMomentManagementService>().CreateAsync(s.Owner.Id,
                new(s.Training.Id,new(new DateOnly(2026,12,15),new TimeOnly(start,0),new TimeOnly(start+2,0),s.Room.Id,10)));
            var results=await s.Race(sp=>Book(sp,reversed?16:14),sp=>Book(sp,reversed?14:16));Assert.All(results,x=>Assert.True(x.Succeeded));
            await s.Read(async sp=>{var rows=await sp.GetRequiredService<ApplicationDbContext>().TrainingMoments.AsNoTracking().OrderBy(x=>x.StartTime).ToListAsync();
                Assert.Equal(2,rows.Count);Assert.Equal(rows[0].EndTime,rows[1].StartTime);Assert.NotEqual(rows[0].Id,rows[1].Id);});
        }
    }
    // Een eigen momentversie voorkomt dat twee planningformulieren elkaar overschrijven, ook wanneer beide losse planningen geldig zijn.
    [Fact] public async Task Same_moment_planning_CAS()
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync();
            Task<TrainingOperationResult> Change(IServiceProvider sp,int end)=>sp.GetRequiredService<TrainingMomentManagementService>().UpdateAsync(s.Owner.Id,
                new(s.Moment!.Id,s.Moment.Version,new(s.Moment.Date,s.Moment.StartTime,new TimeOnly(end,0),s.Room.Id)));
            var results=await s.Race(sp=>Change(sp,reversed?18:17),sp=>Change(sp,reversed?17:18));RaceFixture.OneSuccess(results);
            await s.Read(async sp=>{var row=await sp.GetRequiredService<ApplicationDbContext>().TrainingMoments.AsNoTracking().SingleAsync();
                Assert.Equal(new TimeOnly(reversed?18:17,0),row.EndTime);Assert.NotEqual(s.Moment!.Version,row.Version);Assert.Equal(10,row.MaximumParticipants);});
        }
    }
    // Oude zaal wordt vrijgegeven; een grens die ook vóór verplaatsing geldig is mag in beide volgordes committen.
    [Fact] public async Task Move_and_old_room_capacity_preserve_invariant()
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync();Location? target=null;
            await s.Read(async sp=>target=await TrainingTestData.CreateLocationAsync(sp,"Andere zaal",20));
            Task<TrainingOperationResult> Move(IServiceProvider sp)=>s.Plan(sp,target!.Id);
            Task<TrainingOperationResult> Cap(IServiceProvider sp)=>s.RoomCapacity(sp,10);
            var result=await s.Race(reversed?Cap:Move,reversed?Move:Cap);Assert.All(result,x=>Assert.True(x.Succeeded));
            await s.Read(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();var m=await db.TrainingMoments.AsNoTracking().Include(x=>x.Location).SingleAsync();
                Assert.Equal(target!.Id,m.LocationId);Assert.True(m.MaximumParticipants<=m.Location.MaximumCapacity);
                Assert.Equal(10,(await db.Locations.AsNoTracking().SingleAsync(x=>x.Id==s.Room.Id)).MaximumCapacity);});
        }
    }
    // Beide writes zijn los geldig; de nieuwe zaal mag na wachten nooit het behouden maximum16 onderschrijden.
    [Fact] public async Task Move_versus_new_room_capacity_has_one_winner()
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync(maximum:16);Location? target=null;
            await s.Read(async sp=>target=await TrainingTestData.CreateLocationAsync(sp,"Doelzaal",20));
            Task<TrainingOperationResult> Move(IServiceProvider sp)=>s.Plan(sp,target!.Id);
            Task<TrainingOperationResult> Cap(IServiceProvider sp)=>sp.GetRequiredService<LocationManagementService>().UpdateAsync(s.Admin.Id,new(target!.Id,target.Version,new(target.Name,null,12)));
            RaceFixture.OneSuccess(await s.Race(reversed?Cap:Move,reversed?Move:Cap));
            await s.Read(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();var m=await db.TrainingMoments.AsNoTracking().Include(x=>x.Location).SingleAsync();
                Assert.Equal(reversed?s.Room.Id:target!.Id,m.LocationId);Assert.Equal(16,m.MaximumParticipants);Assert.True(m.MaximumParticipants<=m.Location.MaximumCapacity);
                var room=await db.Locations.AsNoTracking().SingleAsync(x=>x.Id==target!.Id);Assert.Equal(reversed?12:20,room.MaximumCapacity);
                if(reversed)Assert.Equal(s.Moment!.Version,m.Version);else Assert.Equal(target!.Version,room.Version);});
        }
    }
    [Fact] public async Task Room_delete_versus_new_moment()
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync(moment:false);
            Task<TrainingOperationResult> Delete(IServiceProvider sp)=>sp.GetRequiredService<LocationManagementService>().DeleteAsync(s.Admin.Id,new(s.Room.Id,s.Room.Version));
            Task<TrainingOperationResult> Create(IServiceProvider sp)=>sp.GetRequiredService<TrainingMomentManagementService>().CreateAsync(s.Owner.Id,new(s.Training.Id,
                new(new DateOnly(2026,12,15),new TimeOnly(14,0),new TimeOnly(16,0),s.Room.Id,10)));
            RaceFixture.OneSuccess(await s.Race(reversed?Create:Delete,reversed?Delete:Create));
            await s.Read(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();Assert.Equal(reversed?1:0,await db.TrainingMoments.CountAsync());
                Assert.Equal(reversed,await db.Locations.AnyAsync(x=>x.Id==s.Room.Id));});
        }
    }
    [Fact] public async Task Room_delete_versus_planning_move()
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync();Location? target=null;
            await s.Read(async sp=>target=await TrainingTestData.CreateLocationAsync(sp,"Verwijderbare doelzaal",20));
            Task<TrainingOperationResult> Delete(IServiceProvider sp)=>sp.GetRequiredService<LocationManagementService>().DeleteAsync(s.Admin.Id,new(target!.Id,target.Version));
            Task<TrainingOperationResult> Move(IServiceProvider sp)=>s.Plan(sp,target!.Id);
            RaceFixture.OneSuccess(await s.Race(reversed?Move:Delete,reversed?Delete:Move));
            await s.Read(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();var m=await db.TrainingMoments.AsNoTracking().SingleAsync();
                Assert.Equal(reversed?target!.Id:s.Room.Id,m.LocationId);Assert.Equal(reversed,await db.Locations.AnyAsync(x=>x.Id==target!.Id));
                Assert.Equal(s.Moment!.MaximumParticipants,m.MaximumParticipants);});
        }
    }
    // Echte tweede Requested-rij versus verlaging naar1 met reeds één bezette plaats.
    [Fact] public async Task Maximum_versus_requested_fixture_preserves_actual_occupancy()
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync(maximum:2);
            await s.Read(async sp=>await TrainingTestData.CreateRegistrationAsync(sp,s.Moment!));
            var identity=await s.Digest(false);
            Task<TrainingOperationResult> Cap(IServiceProvider sp)=>s.Capacity(sp,1);
            Task<TrainingOperationResult> Request(IServiceProvider sp)=>TrainingTestData.WriteFoundationCoordinatedAsync(sp,s.Training.Id,s.Moment!.Id,s.Applicant);
            RaceFixture.OneSuccess(await s.Race(reversed?Request:Cap,reversed?Cap:Request));
            await s.Read(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();var m=await db.TrainingMoments.AsNoTracking().SingleAsync();
                var count=await db.Registrations.CountAsync(x=>x.Status==RegistrationStatus.Requested||x.Status==RegistrationStatus.Confirmed);
                Assert.Equal(reversed?2:1,count);Assert.Equal(reversed?2:1,m.MaximumParticipants);Assert.True(count<=m.MaximumParticipants);
                Assert.Equal(s.Moment!.Date,m.Date);Assert.Equal(s.Moment.StartTime,m.StartTime);Assert.Equal(s.Moment.LocationId,m.LocationId);
                if(reversed)Assert.Equal(s.Moment.Version,m.Version);});
            Assert.Equal(identity,await s.Digest(false));
        }
    }
    // Architectaanvulling: max10/cap20 → lesgever18 tegenover beheerder12; precies één echte write wint.
    // Na elke schrijfvolgorde moet deelnemersmaximum <= zaalcapaciteit blijven; een test met alleen sequentiële validatie zou deze race niet bewijzen.
    [Fact] public async Task Instructor_increase_versus_same_room_decrease()
    {
        foreach(var roomFirst in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync(maximum:10);var identity=await s.Digest(false);
            Task<TrainingOperationResult> Moment(IServiceProvider sp)=>s.Capacity(sp,18);
            Task<TrainingOperationResult> Room(IServiceProvider sp)=>s.RoomCapacity(sp,12);
            RaceFixture.OneSuccess(await s.Race(roomFirst?Room:Moment,roomFirst?Moment:Room));
            await s.Read(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();var m=await db.TrainingMoments.AsNoTracking().SingleAsync();var r=await db.Locations.AsNoTracking().SingleAsync();
                Assert.Equal(roomFirst?10:18,m.MaximumParticipants);Assert.Equal(roomFirst?12:20,r.MaximumCapacity);Assert.True(m.MaximumParticipants<=r.MaximumCapacity);
                if(roomFirst){Assert.Equal(s.Moment!.Version,m.Version);Assert.NotEqual(s.Room.Version,r.Version);}
                else{Assert.NotEqual(s.Moment!.Version,m.Version);Assert.Equal(s.Room.Version,r.Version);}
                Assert.Equal(s.Moment.Date,m.Date);Assert.Equal(s.Moment.StartTime,m.StartTime);Assert.Equal(s.Moment.EndTime,m.EndTime);
                Assert.Equal(s.Moment.LocationId,m.LocationId);Assert.Equal(0,await db.Registrations.CountAsync());Assert.Equal(0,await db.WaitlistEntries.CountAsync());
                var t=await db.Trainings.AsNoTracking().SingleAsync();Assert.Equal(s.Training.Version,t.Version);Assert.Equal(s.Training.AudienceScope,t.AudienceScope);});
            Assert.Equal(identity,await s.Digest(false));
        }
    }
    // Laat de vaste testklok tijdens echte lockwacht voorbij de opgeslagen start gaan; de later verkregen lock geeft geen recht op een inmiddels begonnen moment.
    [Fact] public async Task Stored_clock_crosses_after_real_write_lock_wait()
    {
        await using var s=await RaceFixture.CreateAsync(today:true);
        var result=await s.Race(sp=>s.RoomCapacity(sp,20),sp=>s.Plan(sp,s.Room.Id),
            ()=>s.Database.Services.GetRequiredService<FixedTimeProvider>().UtcNow=new DateTimeOffset(2026,10,2,12,1,0,TimeSpan.Zero));
        Assert.True(result[0].Succeeded);Assert.Equal(TrainingOperationStatus.Conflict,result[1].Status);
        await s.Read(async sp=>{var m=await sp.GetRequiredService<ApplicationDbContext>().TrainingMoments.AsNoTracking().SingleAsync();
            Assert.Equal(s.Moment!.Version,m.Version);Assert.Equal(s.Moment.StartTime,m.StartTime);Assert.Equal(s.Moment.Date,m.Date);});
    }
}

