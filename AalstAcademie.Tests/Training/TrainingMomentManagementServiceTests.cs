// Sprint 003: Bewijst eigenaar- en actorcontrole, start- en historieregels, overlap, zaalgrens, werkelijke bezetting, versieconflicten en rollback zonder onbedoelde opleidingwijziging.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static AalstAcademie.Tests.Training.TrainingServiceTestSupport;

namespace AalstAcademie.Tests.Training;

/// <summary>53 varianten toetsen vaste eigenaar, eigen scope, planninghistorie, overlap, capaciteit en rollback.</summary>
public sealed class TrainingMomentManagementServiceTests
{
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task Owner_or_admin_creates_distinct_scheduled_moment_without_training_mutation(bool external,bool admin)
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services,external);
        var actor=admin?await TrainingTestData.CreateAdministratorAsync(db.Services):owner;
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var room=await TrainingTestData.CreateLocationAsync(db.Services);
        var identity=await Digest(db,false);var result=await Service(db).CreateAsync(actor.Id,new(training.Id,NewFields(room.Id)));
        Assert.True(result.Succeeded);var stored=await ReadMoment(db,result.Id!.Value);
        Assert.Equal(training.Id,stored.TrainingId);Assert.Equal(room.Id,stored.LocationId);Assert.Equal(TrainingMomentStatus.Scheduled,stored.Status);
        Assert.Equal(12,stored.MaximumParticipants);Assert.NotEqual(Guid.Empty,stored.Version);
        Assert.Equal(training.Version,(await ReadTraining(db,training.Id)).Version);Assert.Equal(identity,await Digest(db,false));
    }

    [Fact]
    public async Task New_moment_preserves_old_history_and_existing_training_audience()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var ids=await Departments(db,1);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner,admin,TrainingAudienceScope.SelectedDepartments,ids);
        var old=await TrainingTestData.CreateMomentAsync(db.Services,training,date:new(2025,1,15));
        var registration=await TrainingTestData.CreateRegistrationAsync(db.Services,old,RegistrationStatus.Refused);
        var buffer=await TrainingTestData.CreateWaitlistEntryAsync(db.Services,training,true);
        var created=await TrainingTestData.CreateMomentAsync(db.Services,training);
        Assert.NotEqual(old.Id,created.Id);Assert.Equal(old.Version,(await ReadMoment(db,old.Id)).Version);
        var after=await ReadTraining(db,training.Id);Assert.Equal(training.Version,after.Version);Assert.Equal(training.AudienceScope,after.AudienceScope);
        Assert.Equal(ids,after.TrainingDepartments.Select(x=>x.DepartmentId).ToArray());
        await using var read=db.CreateScope();var context=read.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var actualRegistration=await context.Registrations.AsNoTracking().SingleAsync(x=>x.Id==registration.Id);
        Assert.Equal(registration.Version,actualRegistration.Version);Assert.Equal(RegistrationStatus.Refused,actualRegistration.Status);Assert.Equal(old.Id,actualRegistration.TrainingMomentId);
        var actualBuffer=await context.WaitlistEntries.AsNoTracking().SingleAsync(x=>x.Id==buffer.Id);
        Assert.Equal(buffer.Version,actualBuffer.Version);Assert.Equal(buffer.ClosedAtUtc,actualBuffer.ClosedAtUtc);
    }

    [Theory] [InlineData("blocked")] [InlineData("roleless")] [InlineData("Pending")] [InlineData("Refused")]
    [InlineData("missing-profile")] [InlineData("dual-profile")] [InlineData("missing-type")]
    public async Task D76_new_moment_requires_current_eligibility_of_fixed_training_owner(string variant)
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var room=await TrainingTestData.CreateLocationAsync(db.Services);
        await MakeOwnerIneligible(db,owner.Id,variant);var before=await Digest(db);
        Assert.Equal(TrainingOperationStatus.Conflict,(await Service(db).CreateAsync(admin.Id,new(training.Id,NewFields(room.Id)))).Status);
        Assert.Equal(before,await Digest(db));
    }

    [Theory] [InlineData("missing")] [InlineData("Pending")] [InlineData("Refused")] [InlineData("blocked")] [InlineData("roleless")] [InlineData("demo-outside")]
    public async Task Current_actor_denial_blocks_all_moment_writes(string variant)
    {
        await using var db=await Fixture(demo:variant!="demo-outside");var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);
        var actor=await TrainingTestData.CreateAdministratorAsync(db.Services);await DenyActor(db,actor.Id,variant);
        var id=variant=="missing"?"ontbrekend":actor.Id;var before=await Digest(db);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Service(db).CreateAsync(id,new(training.Id,NewFields(moment.LocationId)))).Status);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Service(db).UpdateAsync(id,new(moment.Id,moment.Version,Planning(moment)))).Status);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Service(db).ChangeCapacityAsync(id,new(moment.Id,moment.Version,16))).Status);
        Assert.Equal(before,await Digest(db));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Foreign_instructor_gets_notfound_before_invalid_field_information(bool external)
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var foreign=await TrainingTestData.CreateOwnerAsync(db.Services,external);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);var before=await Digest(db);
        Assert.Equal(TrainingOperationStatus.NotFound,(await Service(db).CreateAsync(foreign.Id,new(training.Id,new(null,null,null,null,null)))).Status);
        foreach(var id in new[]{moment.Id,int.MaxValue})
        {
            Assert.Equal(TrainingOperationStatus.NotFound,(await Service(db).UpdateAsync(foreign.Id,new(id,null,new(null,null,null,null)))).Status);
            Assert.Equal(TrainingOperationStatus.NotFound,(await Service(db).ChangeCapacityAsync(foreign.Id,new(id,null,null))).Status);
        }
        Assert.Equal(before,await Digest(db));
    }

    [Theory] [InlineData(14,16,false)] [InlineData(13,17,false)] [InlineData(14,15,false)] [InlineData(13,15,false)]
    [InlineData(15,17,false)] [InlineData(12,14,true)] [InlineData(16,18,true)]
    public async Task Overlap_uses_strict_interval_intersection_across_different_trainings(int start,int end,bool allowed)
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var first=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var other=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var occupied=await TrainingTestData.CreateMomentAsync(db.Services,first);var before=await Digest(db);
        var result=await Service(db).CreateAsync(owner.Id,new(other.Id,NewFields(occupied.LocationId) with {StartTime=new(start,0),EndTime=new(end,0)}));
        Assert.Equal(allowed?TrainingOperationStatus.Success:TrainingOperationStatus.Conflict,result.Status);
        if(!allowed)Assert.Equal(before,await Digest(db));
    }

    [Fact]
    public async Task Same_interval_on_other_day_or_other_room_is_allowed()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var first=await TrainingTestData.CreateMomentAsync(db.Services,training);var room=await TrainingTestData.CreateLocationAsync(db.Services,"Andere zaal");
        Assert.True((await Service(db).CreateAsync(owner.Id,new(training.Id,NewFields(first.LocationId) with {Date=first.Date.AddDays(1)}))).Succeeded);
        Assert.True((await Service(db).CreateAsync(owner.Id,new(training.Id,NewFields(room.Id)))).Succeeded);
    }

    [Fact]
    public async Task Cancelled_history_does_not_book_the_room()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var old=await TrainingTestData.CreateMomentAsync(db.Services,training,status:TrainingMomentStatus.Cancelled);
        Assert.True((await Service(db).CreateAsync(owner.Id,new(training.Id,NewFields(old.LocationId)))).Succeeded);
        Assert.Equal(TrainingMomentStatus.Cancelled,(await ReadMoment(db,old.Id)).Status);
    }

    [Fact]
    public async Task Own_id_is_excluded_and_valid_room_move_preserves_maximum()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);
        Assert.True((await Service(db).UpdateAsync(owner.Id,new(moment.Id,moment.Version,Planning(moment)))).Succeeded);
        var current=await ReadMoment(db,moment.Id);var destination=await TrainingTestData.CreateLocationAsync(db.Services,"Nieuwe passende zaal",16);
        Assert.True((await Service(db).UpdateAsync(owner.Id,new(current.Id,current.Version,Planning(current) with {LocationId=destination.Id}))).Succeeded);
        var after=await ReadMoment(db,moment.Id);Assert.Equal(destination.Id,after.LocationId);Assert.Equal(12,after.MaximumParticipants);
    }

    [Theory] [InlineData(RegistrationStatus.Requested)] [InlineData(RegistrationStatus.Confirmed)]
    [InlineData(RegistrationStatus.Refused)] [InlineData(RegistrationStatus.Cancelled)]
    public async Task Any_registration_status_blocks_changed_and_unchanged_planning_commands(RegistrationStatus status)
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);
        await TrainingTestData.CreateRegistrationAsync(db.Services,moment,status);var before=await Digest(db);
        foreach(var actor in new[]{owner,admin})
        {
            // Een ongewijzigde herpost is geen toestemming om de historisch geblokkeerde planningversie te roteren.
            Assert.Equal(TrainingOperationStatus.Conflict,
                (await Service(db).UpdateAsync(actor.Id,new(moment.Id,moment.Version,Planning(moment)))).Status);
            Assert.Equal(TrainingOperationStatus.Conflict,
                (await Service(db).UpdateAsync(actor.Id,new(moment.Id,moment.Version,Planning(moment) with {Date=moment.Date.AddDays(1)}))).Status);
        }
        Assert.Equal(before,await Digest(db));
    }

    [Fact]
    public async Task Registration_on_other_moment_does_not_lock_this_moment_planning()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var old=await TrainingTestData.CreateMomentAsync(db.Services,training);await TrainingTestData.CreateRegistrationAsync(db.Services,old);
        var current=await TrainingTestData.CreateMomentAsync(db.Services,training,date:old.Date.AddDays(1));
        Assert.True((await Service(db).UpdateAsync(owner.Id,new(current.Id,current.Version,Planning(current) with {Date=current.Date.AddDays(1)}))).Succeeded);
        Assert.Equal(old.Version,(await ReadMoment(db,old.Id)).Version);
    }

    [Theory] [InlineData(-1)] [InlineData(0)] [InlineData(1)]
    public async Task Stored_start_guards_planning_and_capacity_against_posted_future_bypass(int relativeTick)
    {
        foreach(var capacity in new[]{false,true})
        {
            await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
            var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);
            db.Services.GetRequiredService<FixedTimeProvider>().UtcNow=StartUtc(db,moment).AddTicks(relativeTick);var before=await Digest(db);
            var result=capacity?await Service(db).ChangeCapacityAsync(owner.Id,new(moment.Id,moment.Version,16)):
                await Service(db).UpdateAsync(owner.Id,new(moment.Id,moment.Version,Planning(moment) with {Date=moment.Date.AddYears(2)}));
            Assert.Equal(relativeTick<0?TrainingOperationStatus.Success:TrainingOperationStatus.Conflict,result.Status);
            if(relativeTick>=0)Assert.Equal(before,await Digest(db));
        }
    }

    [Fact]
    public async Task Cancelled_moment_cannot_be_edited_or_implicitly_reactivated()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training,status:TrainingMomentStatus.Cancelled);var before=await Digest(db);
        Assert.Equal(TrainingOperationStatus.Conflict,(await Service(db).UpdateAsync(owner.Id,new(moment.Id,moment.Version,Planning(moment)))).Status);
        Assert.Equal(TrainingOperationStatus.Conflict,(await Service(db).ChangeCapacityAsync(owner.Id,new(moment.Id,moment.Version,16))).Status);
        Assert.Equal(before,await Digest(db));
    }

    [Fact]
    public async Task Room_move_to_smaller_capacity_never_silently_lowers_moment_maximum()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training,maximumParticipants:16);
        var destination=await TrainingTestData.CreateLocationAsync(db.Services,"Kleinere zaal",12);var before=await Digest(db);
        Assert.Equal(TrainingOperationStatus.Conflict,(await Service(db).UpdateAsync(owner.Id,new(moment.Id,moment.Version,Planning(moment) with {LocationId=destination.Id}))).Status);
        Assert.Equal(before,await Digest(db));Assert.Equal(16,(await ReadMoment(db,moment.Id)).MaximumParticipants);
    }

    [Theory] [InlineData(1,false)] [InlineData(2,true)] [InlineData(25,false)]
    public async Task Capacity_minimum_counts_requested_and_confirmed_for_this_moment_only(int maximum,bool allowed)
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);
        foreach(var status in Enum.GetValues<RegistrationStatus>())await TrainingTestData.CreateRegistrationAsync(db.Services,moment,status);
        var other=await TrainingTestData.CreateMomentAsync(db.Services,training,date:moment.Date.AddDays(1));
        await TrainingTestData.CreateRegistrationAsync(db.Services,other);var before=await Digest(db);
        var result=await Service(db).ChangeCapacityAsync(owner.Id,new(moment.Id,moment.Version,maximum));
        Assert.Equal(allowed?TrainingOperationStatus.Success:TrainingOperationStatus.Conflict,result.Status);
        if(!allowed)Assert.Equal(before,await Digest(db));
        var after=await ReadMoment(db,moment.Id);Assert.Equal(moment.Date,after.Date);Assert.Equal(moment.StartTime,after.StartTime);
        Assert.Equal(moment.EndTime,after.EndTime);Assert.Equal(moment.LocationId,after.LocationId);
        Assert.Equal(allowed?maximum:12,after.MaximumParticipants);
    }

    [Fact]
    public async Task Empty_moment_still_requires_minimum_one()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);var before=await Digest(db);
        Assert.Equal(TrainingOperationStatus.Invalid,(await Service(db).ChangeCapacityAsync(owner.Id,new(moment.Id,moment.Version,0))).Status);
        Assert.Equal(before,await Digest(db));Assert.True((await Service(db).ChangeCapacityAsync(owner.Id,new(moment.Id,moment.Version,1))).Succeeded);
    }

    [Theory] [InlineData("blocked")] [InlineData("roleless")]
    public async Task Admin_can_manage_existing_moment_after_owner_loses_eligibility_without_repair(string variant)
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);
        await MakeOwnerIneligible(db,owner.Id,variant);var identity=await Digest(db,false);
        Assert.True((await Service(db).UpdateAsync(admin.Id,new(moment.Id,moment.Version,Planning(moment) with {Date=moment.Date.AddDays(1)}))).Succeeded);
        var after=await ReadMoment(db,moment.Id);Assert.True((await Service(db).ChangeCapacityAsync(admin.Id,new(after.Id,after.Version,16))).Succeeded);
        Assert.Equal(identity,await Digest(db,false));
    }

    [Theory] [InlineData("missing")] [InlineData("empty")] [InlineData("stale")]
    public async Task Moment_original_version_is_required_without_retry_or_auto_upgrade(string variant)
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);var before=await Digest(db);var version=Version(variant);
        Assert.Equal(TrainingOperationStatus.Conflict,(await Service(db).UpdateAsync(owner.Id,new(moment.Id,version,Planning(moment)))).Status);
        Assert.Equal(TrainingOperationStatus.Conflict,(await Service(db).ChangeCapacityAsync(owner.Id,new(moment.Id,version,16))).Status);
        Assert.Equal(before,await Digest(db));
    }

    [Fact]
    public async Task Clock_crosses_stored_start_before_actual_write()
    {
        var clock=new SequenceClock();await using var db=await FileSqliteTestDatabase.CreateAsync(configureServices:s=>{
            TrainingTestServices.Configure(s);s.RemoveAll<TimeProvider>();s.AddSingleton<TimeProvider>(clock);});
        await TrainingTestData.InitializeAsync(db.Services);var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);
        var before=await Digest(db);var start=StartUtc(db,moment);clock.Arm(start.AddTicks(-1),start);
        Assert.Equal(TrainingOperationStatus.Conflict,(await Service(db).ChangeCapacityAsync(owner.Id,new(moment.Id,moment.Version,16))).Status);
        Assert.True(clock.Reads>=2);Assert.Equal(before,await Digest(db));
    }

    [Theory] [InlineData("moment-create")] [InlineData("moment-planning")] [InlineData("moment-capacity")]
    public async Task Actual_late_moment_sql_failure_rolls_back_everything_and_allows_clean_retry(string point)
    {
        var fault=new TrainingFailureInterceptor();await using var db=await Fixture(fault);var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var room=await TrainingTestData.CreateLocationAsync(db.Services);
        var moment=point=="moment-create"?null:await TrainingTestData.CreateMomentAsync(db.Services,training,location:room);var before=await Digest(db);
        Task<TrainingOperationResult> Act()=>moment is null?Service(db).CreateAsync(owner.Id,new(training.Id,NewFields(room.Id))):
            point=="moment-planning"?Service(db).UpdateAsync(owner.Id,new(moment.Id,moment.Version,Planning(moment) with {Date=moment.Date.AddDays(1)})):
            Service(db).ChangeCapacityAsync(owner.Id,new(moment.Id,moment.Version,16));
        fault.Arm(point);Assert.Equal(TrainingOperationStatus.StorageUnavailable,(await Act()).Status);
        Assert.True(fault.Triggered);Assert.True(fault.SawSqlWrite);Assert.Equal(before,await Digest(db));
        fault.Disarm();Assert.True((await Act()).Succeeded);
    }
    private static TrainingMomentManagementService Service(FileSqliteTestDatabase db)=>db.Services.GetRequiredService<TrainingMomentManagementService>();
    private static TrainingMomentFields NewFields(int roomId)=>new(new(2026,12,15),new(14,0),new(16,0),roomId,12);
    // Geen sleep of systeemklok: de tweede echte guardlezing passeert precies de opgeslagen grens.
    private sealed class SequenceClock:TimeProvider
    {
        private Queue<DateTimeOffset>? values;private DateTimeOffset last=new(2026,10,2,12,0,0,TimeSpan.Zero);
        public int Reads{get;private set;}
        public void Arm(params DateTimeOffset[] sequence){values=new(sequence);Reads=0;}
        public override DateTimeOffset GetUtcNow(){if(values is not null){Reads++;if(values.Count>0)last=values.Dequeue();}return last;}
    }
}
