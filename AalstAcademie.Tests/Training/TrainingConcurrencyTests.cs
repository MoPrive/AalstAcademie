// Sprint 003: Laat onafhankelijke verbindingen concurreren voor voorwaarden tegenover eerste historie, doelgroep tegenover historie en categorieën
// Beide schrijfvolgordes krijgen een onafhankelijke opslagcontrole.
using System.Data.Common;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Tests.Training;

/// <summary>Zeven echte provider-races: beide schrijfvolgordes, vooraf gelezen versies en onafhankelijke readbacks.</summary>
[Collection("Revision02 provider races")]
public class TrainingConcurrencyTests
{
    // Voorwaarden en eerste echte historie mogen nooit samen op de oude voorwaardenlezing wijzigen.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Terms_versus_first_registration_or_buffer(bool buffer)
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync();
            var identity=await s.Digest(false);
            Task<TrainingOperationResult> Terms(IServiceProvider sp)=>sp.GetRequiredService<TrainingManagementService>().UpdateAsync(s.Owner.Id,
                new(s.Training.Id,s.Training.Version,new(s.Training.Title,s.Training.Description,s.Training.CategoryId,null,true)));
            Task<TrainingOperationResult> History(IServiceProvider sp)=>TrainingTestData.WriteFoundationCoordinatedAsync(sp,s.Training.Id,
                buffer?null:s.Moment!.Id,s.Applicant,s.Training.Version);
            var results=await s.Race(reversed?History:Terms,reversed?Terms:History);
            RaceFixture.OneSuccess(results);
            await s.Read(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();var t=await db.Trainings.AsNoTracking().SingleAsync();
                Assert.Equal(!reversed,t.RequiresMotivation);
                if(reversed)Assert.Equal(s.Training.Version,t.Version);else Assert.NotEqual(s.Training.Version,t.Version);
                Assert.Equal(reversed?1:0,buffer?await db.WaitlistEntries.CountAsync():await db.Registrations.CountAsync());
                Assert.Equal(s.Training.InstructorUserId,t.InstructorUserId);Assert.Equal(s.Training.AudienceScope,t.AudienceScope);});
            Assert.Equal(identity,await s.Digest(false));
        }
    }
    // Doelgroepwijziging en historieaanmaak mogen beide slagen; status/FKs/voorwaarden worden niet herschreven.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Audience_versus_registration_or_buffer(bool buffer)
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync();
            var identity=await s.Digest(false);
            Task<TrainingOperationResult> Audience(IServiceProvider sp)=>sp.GetRequiredService<TrainingManagementService>().AssignAudienceAsync(s.Admin.Id,
                new(s.Training.Id,s.Training.Version,TrainingAudienceScope.AllDepartments,[]));
            Task<TrainingOperationResult> History(IServiceProvider sp)=>TrainingTestData.WriteFoundationCoordinatedAsync(sp,s.Training.Id,buffer?null:s.Moment!.Id,s.Applicant);
            var result=await s.Race(reversed?History:Audience,reversed?Audience:History);
            Assert.All(result,x=>Assert.True(x.Succeeded));
            await s.Read(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();var t=await db.Trainings.AsNoTracking().SingleAsync();
                Assert.Equal(TrainingAudienceScope.AllDepartments,t.AudienceScope);Assert.False(t.RequiresMotivation);Assert.Null(t.ExternalTotalPriceEuros);
                if(buffer){var row=await db.WaitlistEntries.AsNoTracking().SingleAsync();Assert.Equal(t.Id,row.TrainingId);
                    if(reversed){Assert.NotNull(row.ClosedAtUtc);var converted=await db.Registrations.AsNoTracking().SingleAsync();Assert.Equal(row.ApplicationUserId,converted.ApplicationUserId);Assert.Equal(s.Moment!.Id,converted.TrainingMomentId);Assert.Equal(RegistrationStatus.Requested,converted.Status);Assert.Equal(row.Motivation,converted.Motivation);}
                    else {Assert.Null(row.ClosedAtUtc);Assert.Empty(await db.Registrations.ToListAsync());}}
                else {var row=await db.Registrations.AsNoTracking().SingleAsync();Assert.Equal(s.Moment!.Id,row.TrainingMomentId);Assert.Equal(RegistrationStatus.Requested,row.Status);}
                Assert.Equal(0,await db.TrainingDepartments.CountAsync());});
            Assert.Equal(identity,await s.Digest(false));
        }
    }
    [Fact] public async Task Category_rename_CAS()
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync();
            Guid version=Guid.Empty;await s.Read(async sp=>version=await sp.GetRequiredService<ApplicationDbContext>().Categories
                .Where(x=>x.Id==s.Training.CategoryId).Select(x=>EF.Property<Guid>(x,"Version")).SingleAsync());
            Task<TrainingOperationResult> A(IServiceProvider sp)=>sp.GetRequiredService<CategoryManagementService>().RenameAsync(s.Admin.Id,new(s.Training.CategoryId,version,"Naam A"));
            Task<TrainingOperationResult> B(IServiceProvider sp)=>sp.GetRequiredService<CategoryManagementService>().RenameAsync(s.Admin.Id,new(s.Training.CategoryId,version,"Naam B"));
            var result=await s.Race(reversed?B:A,reversed?A:B);RaceFixture.OneSuccess(result);
            await s.Read(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();var c=await db.Categories.SingleAsync();
                Assert.Equal(reversed?"Naam B":"Naam A",c.Name);Assert.NotEqual(version,db.Entry(c).Property<Guid>("Version").CurrentValue);
                Assert.Equal(s.Training.Version,(await db.Trainings.AsNoTracking().SingleAsync()).Version);});
        }
    }
    // Normalisatieuniciteit wordt ook na een echte lockwacht herlezen; create/create en create/rename.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Category_unique_name_races(bool rename)
    {
        foreach(var reversed in new[]{false,true})
        {
            await using var s=await RaceFixture.CreateAsync();Guid version=Guid.Empty;
            await s.Read(async sp=>version=await sp.GetRequiredService<ApplicationDbContext>().Categories.Select(x=>EF.Property<Guid>(x,"Version")).SingleAsync());
            Task<TrainingOperationResult> A(IServiceProvider sp)=>sp.GetRequiredService<CategoryManagementService>().CreateAsync(s.Admin.Id,new("  Gedeelde   naam "));
            Task<TrainingOperationResult> B(IServiceProvider sp)=>rename?sp.GetRequiredService<CategoryManagementService>().RenameAsync(s.Admin.Id,new(s.Training.CategoryId,version,"GEDEELDE NAAM")):
                sp.GetRequiredService<CategoryManagementService>().CreateAsync(s.Admin.Id,new("GEDEELDE NAAM"));
            var result=await s.Race(reversed?B:A,reversed?A:B);
            Assert.Single(result,x=>x.Succeeded);Assert.Single(result,x=>x.Status==TrainingOperationStatus.Invalid);
            await s.Read(async sp=>Assert.Equal(1,await sp.GetRequiredService<ApplicationDbContext>().Categories.CountAsync(x=>EF.Property<string>(x,"NormalizedName")=="GEDEELDE NAAM")));
        }
    }
}

/// <summary>Een SQL-write pauzeert werkelijk binnen de eerste transactie; de tweede eigen verbinding moet op die lock wachten.</summary>
internal sealed class RaceSqlGate : DbCommandInterceptor
{
    private int armed;
    public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool SawRealWrite {get;private set;}
    // De interceptor pauzeert eenmaal na echte SQL. Zo concurreert de tweede verbinding met een al actieve schrijftransactie.
    public void Arm()=>Interlocked.Exchange(ref armed,1);
    private async Task After(DbCommand command)
    {
        if(!command.CommandText.Contains("INSERT INTO",StringComparison.Ordinal)&&!command.CommandText.Contains("UPDATE ",StringComparison.Ordinal)&&
            !command.CommandText.Contains("DELETE FROM",StringComparison.Ordinal))return;
        if(Interlocked.Exchange(ref armed,0)!=1)return;
        SawRealWrite=true;Entered.TrySetResult();await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData data,DbDataReader result,CancellationToken token=default)
    {try { await After(command); return result; } catch { await result.DisposeAsync(); throw; }}
    public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command,CommandExecutedEventData data,int result,CancellationToken token=default)
    {await After(command);return result;}
}

/// <summary>Iedere race bezit één eigen GUID-database; setup/scopes gebruiken uitsluitend de echte diensten.</summary>
internal sealed class RaceFixture : IAsyncDisposable
{
    public FileSqliteTestDatabase Database {get;private init;}=null!;
    public RaceSqlGate Gate {get;private init;}=null!;
    public ApplicationUser Owner {get;private init;}=null!;
    public ApplicationUser Admin {get;private init;}=null!;
    public ApplicationUser Applicant {get;private init;}=null!;
    public TrainingEntity Training {get;private init;}=null!;
    public Location Room {get;private init;}=null!;
    public TrainingMoment? Moment {get;private init;}
    public static async Task<RaceFixture> CreateAsync(bool moment=true,int maximum=10,bool today=false)
    {
        var gate=new RaceSqlGate();var db=await FileSqliteTestDatabase.CreateAsync(interceptor:gate,configureServices:sp=>TrainingTestServices.Configure(sp));
        await TrainingTestData.InitializeAsync(db.Services);var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var applicant=await TestAccountData.CreateAsync(db.Services,RequestedAccountType.Employee,AccountApprovalStatus.Approved,roles:[RoleNames.Medewerker]);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var room=await TrainingTestData.CreateLocationAsync(db.Services,maximumCapacity:20);
        var execution=moment?await TrainingTestData.CreateMomentAsync(db.Services,training,location:room,maximumParticipants:maximum,
            date:today?new DateOnly(2026,10,2):null,start:today?new TimeOnly(14,1):null,end:today?new TimeOnly(14,10):null):null;
        return new(){Database=db,Gate=gate,Owner=owner,Admin=admin,Applicant=applicant,Training=training,Room=room,Moment=execution};
    }
    public async Task Read(Func<IServiceProvider,Task> read){await using var scope=Database.CreateScope();await read(scope.ServiceProvider);}
    public async Task<string> Digest(bool features=true){string digest="";await Read(async sp=>digest=await TrainingTestData.DigestAsync(sp,features));return digest;}
    public async Task<TrainingOperationResult[]> Race(Func<IServiceProvider,Task<TrainingOperationResult>> first,Func<IServiceProvider,Task<TrainingOperationResult>> second,Action? whileLocked=null)
    {
        await using var a=Database.CreateScope();await using var b=Database.CreateScope();
        var ca=a.ServiceProvider.GetRequiredService<ApplicationDbContext>();var cb=b.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await ca.Database.OpenConnectionAsync();await cb.Database.OpenConnectionAsync();
        Assert.NotSame(ca.Database.GetDbConnection(),cb.Database.GetDbConnection());
        Assert.Equal(ca.Database.GetDbConnection().DataSource,cb.Database.GetDbConnection().DataSource);
        Gate.Arm();var firstTask=Task.Run(()=>first(a.ServiceProvider));Task<TrainingOperationResult>? secondTask=null;
        Exception? primaryFailure = null;
        try{
            await Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var attempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            secondTask=Task.Run(()=>{attempted.SetResult();return second(b.ServiceProvider);});
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));await Task.Delay(150);
            Assert.False(secondTask.IsCompleted);Assert.True(Gate.SawRealWrite);whileLocked?.Invoke();
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            // Ook bij een assertionfout krijgen gestarte schrijvers eerst hun einde; scopes sluiten pas daarna.
            Gate.Release.TrySetResult();
            var started = secondTask is null ? new[] { firstTask } : new[] { firstTask, secondTask };
            try { await Task.WhenAll(started).WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception cleanup) when (primaryFailure is not null)
            { throw new AggregateException("De provider-race faalde en kon niet veilig afronden.", primaryFailure, cleanup); }
        }
        return await Task.WhenAll(firstTask,secondTask!).WaitAsync(TimeSpan.FromSeconds(10));
    }
    public static void OneSuccess(IEnumerable<TrainingOperationResult> result)
    {Assert.Single(result,x=>x.Succeeded);Assert.Single(result,x=>x.Status==TrainingOperationStatus.Conflict);}
    public Task<TrainingOperationResult> Plan(IServiceProvider sp,int room,Guid? version=null)=>sp.GetRequiredService<TrainingMomentManagementService>().UpdateAsync(Owner.Id,
        new(Moment!.Id,version??Moment.Version,new(Moment.Date,Moment.StartTime,Moment.EndTime,room)));
    public Task<TrainingOperationResult> Capacity(IServiceProvider sp,int maximum)=>sp.GetRequiredService<TrainingMomentManagementService>().ChangeCapacityAsync(Owner.Id,new(Moment!.Id,Moment.Version,maximum));
    public Task<TrainingOperationResult> RoomCapacity(IServiceProvider sp,int maximum)=>sp.GetRequiredService<LocationManagementService>().UpdateAsync(Admin.Id,new(Room.Id,Room.Version,new(Room.Name,Room.Address,maximum)));
    public async ValueTask DisposeAsync()=>await Database.DisposeAsync();
}

