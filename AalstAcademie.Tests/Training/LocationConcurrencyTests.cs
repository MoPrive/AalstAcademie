// Sprint 003: Toetst twee zaalwrites met dezelfde versie, delete tegenover edit en een echte vijfseconden-locktimeout
// Na weigering blijven data en timeoutinstellingen herstelbaar.
using System.Diagnostics;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Services.Training;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Training;

/// <summary>Drie echte zaalraces toetsen CAS, referentievrije delete/update en de begrensde locktimeout met herstel.</summary>
[Collection("Revision02 provider races")]
public class LocationConcurrencyTests
{
    // Beide schrijvers gebruiken dezelfde vooraf gelezen versie; de onafhankelijke readback moet precies de eerste geslaagde capaciteit vinden.
    [Fact] public async Task Capacity_update_CAS_has_one_winner()
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync(moment:false);
            RaceFixture.OneSuccess(await s.Race(sp=>s.RoomCapacity(sp,reversed?18:16),sp=>s.RoomCapacity(sp,reversed?16:18)));
            await s.Read(async sp=>{var row=await sp.GetRequiredService<ApplicationDbContext>().Locations.AsNoTracking().SingleAsync();
                Assert.Equal(reversed?18:16,row.MaximumCapacity);Assert.NotEqual(s.Room.Version,row.Version);});
        }
    }
    // Een verloren update mag een verwijderde zaal niet opnieuw aanmaken. Omgekeerde volgorde moet delete als stale weigeren.
    [Fact] public async Task Delete_update_CAS_does_not_resurrect_room()
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync(moment:false);
            Task<TrainingOperationResult> Delete(IServiceProvider sp)=>sp.GetRequiredService<LocationManagementService>().DeleteAsync(s.Admin.Id,new(s.Room.Id,s.Room.Version));
            Task<TrainingOperationResult> Update(IServiceProvider sp)=>s.RoomCapacity(sp,18);
            var result=await s.Race(reversed?Update:Delete,reversed?Delete:Update);
            Assert.Single(result,x=>x.Succeeded);Assert.Equal(reversed?TrainingOperationStatus.Conflict:TrainingOperationStatus.NotFound,result[1].Status);
            await s.Read(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();Assert.Equal(reversed,await db.Locations.AnyAsync(x=>x.Id==s.Room.Id));
                Assert.Equal(0,await db.TrainingMoments.CountAsync());Assert.Equal(s.Training.Version,(await db.Trainings.AsNoTracking().SingleAsync()).Version);});
        }
    }
    // Houd een echte providerlock vast en meet de begrensde wachttijd. Na vrijgave moet retry met dezelfde oorspronkelijke versie nog kunnen slagen.
    [Fact] public async Task Held_lock_returns_503_in_five_seconds_restores_timeouts_and_allows_original_retry()
    {
        await using var s=await RaceFixture.CreateAsync(moment:false);var before=await s.Digest();
        await using var holder=new SqliteConnection(s.Database.ConnectionString);await holder.OpenAsync();using var held=holder.BeginTransaction(deferred:false);
        await using var scope=s.Database.CreateScope();var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connection=(SqliteConnection)db.Database.GetDbConnection();connection.DefaultTimeout=17;db.Database.SetCommandTimeout(13);
        var timer=Stopwatch.StartNew();var result=await Task.Run(()=>s.RoomCapacity(scope.ServiceProvider,18));timer.Stop();
        Assert.Equal(TrainingOperationStatus.StorageUnavailable,result.Status);Assert.InRange(timer.Elapsed.TotalSeconds,4,8);
        Assert.Equal(17,connection.DefaultTimeout);Assert.Equal(13,db.Database.GetCommandTimeout());Assert.Equal(before,await s.Digest());
        held.Rollback();
        var retry=await s.RoomCapacity(scope.ServiceProvider,18);Assert.True(retry.Succeeded);
        await s.Read(async sp=>Assert.Equal(18,(await sp.GetRequiredService<ApplicationDbContext>().Locations.AsNoTracking().SingleAsync()).MaximumCapacity));
    }
}

