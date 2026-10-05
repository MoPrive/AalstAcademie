// Sprint 003: Bewijst actuele serviceautorisatie, vaste eigenaar, Unassigned-aanmaak, behoud bij ontbrekende velden, historische voorwaardenlock en atomische doelgroepwijziging.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;
using static AalstAcademie.Tests.Training.TrainingServiceTestSupport;

namespace AalstAcademie.Tests.Training;

/// <summary>53 varianten bewijzen huidige rechten, definitievoorwaarden, historie en volledige providerrollback.</summary>
public sealed class TrainingManagementServiceTests
{
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task Create_uses_fixed_user_owner_and_starts_unassigned(bool external, bool admin)
    {
        await using var db = await Fixture();
        var owner = await TrainingTestData.CreateOwnerAsync(db.Services, external);
        var actor = admin ? await TrainingTestData.CreateAdministratorAsync(db.Services) : owner;
        var fields = await TrainingTestData.FieldsAsync(db.Services, external);
        var identity = await Digest(db, false);
        var result = await Management(db).CreateAsync(actor.Id, new(fields, admin ? owner.Id : null));
        Assert.True(result.Succeeded);
        var stored = await ReadTraining(db, result.Id!.Value);
        Assert.Equal(owner.Id, stored.InstructorUserId);
        Assert.Equal(TrainingAudienceScope.Unassigned, stored.AudienceScope); Assert.Empty(stored.TrainingDepartments);
        Assert.Equal(fields.Title, stored.Title); Assert.Equal(fields.Description, stored.Description);
        Assert.Equal(fields.CategoryId, stored.CategoryId); Assert.Equal(fields.ExternalTotalPriceEuros, stored.ExternalTotalPriceEuros);
        Assert.NotEqual(Guid.Empty, stored.Version); Assert.Equal(identity, await Digest(db, false));
    }

    [Theory]
    [InlineData("missing")] [InlineData("Pending")] [InlineData("Refused")] [InlineData("blocked")] [InlineData("roleless")]
    [InlineData("missing-profile")] [InlineData("opposite-profile")] [InlineData("dual-profile")]
    [InlineData("missing-type")] [InlineData("unknown-type")] [InlineData("invalid-name")] [InlineData("department")] [InlineData("organization")]
    public async Task Create_rejects_current_ineligible_owner_without_repair(string variant)
    {
        await using var db = await Fixture();
        var admin = await TrainingTestData.CreateAdministratorAsync(db.Services);
        var owner = await TrainingTestData.CreateOwnerAsync(db.Services, variant == "organization");
        var fields = await TrainingTestData.FieldsAsync(db.Services, variant == "organization");
        await MakeOwnerIneligible(db, owner.Id, variant);
        var before = await Digest(db);
        Assert.Equal(TrainingOperationStatus.Invalid, (await Management(db).CreateAsync(admin.Id,
            new(fields, variant == "missing" ? "niet-bestaand" : owner.Id))).Status);
        Assert.Equal(before, await Digest(db));
    }

    [Fact]
    public async Task Legacy_missing_manager_remains_eligible_without_backfill()
    {
        await using var db = await Fixture(); var owner = await TrainingTestData.CreateOwnerAsync(db.Services);
        Assert.Null(owner.ManagerUserId);
        Assert.True((await Management(db).CreateAsync(owner.Id, new(await TrainingTestData.FieldsAsync(db.Services)))).Succeeded);
        Assert.Null((await db.Context.Users.AsNoTracking().SingleAsync(x => x.Id == owner.Id)).ManagerUserId);
    }

    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task Historical_metadata_remains_editable_and_preserves_moments(bool external, bool admin)
    {
        await using var db = await Fixture(); var owner = await TrainingTestData.CreateOwnerAsync(db.Services, external);
        var actor = admin ? await TrainingTestData.CreateAdministratorAsync(db.Services) : owner;
        var training = await TrainingTestData.CreateTrainingAsync(db.Services, owner);
        var moment = await TrainingTestData.CreateMomentAsync(db.Services, training, date: new(2025, 1, 15));
        await TrainingTestData.CreateRegistrationAsync(db.Services, moment, RegistrationStatus.Refused);
        var category = await TrainingTestData.CreateCategoryAsync(db.Services, "Nieuwe historische categorie");
        var identity = await Digest(db, false);
        var result = await Management(db).UpdateAsync(actor.Id, new(training.Id, training.Version,
            Fields(training) with { Title = "Nieuwe titel", Description = "Regels\n<script>tekst</script>", CategoryId = category.Id }));
        Assert.True(result.Succeeded);
        var after = await ReadTraining(db, training.Id);
        Assert.Equal("Nieuwe titel", after.Title); Assert.Equal(category.Id, after.CategoryId);
        Assert.Equal("Regels\n<script>tekst</script>", after.Description);
        Assert.Equal(training.InstructorUserId, after.InstructorUserId); Assert.NotEqual(training.Version, after.Version);
        Assert.Equal(moment.Version, (await ReadMoment(db, moment.Id)).Version);
        Assert.Equal(identity, await Digest(db, false));
    }

    [Theory] [InlineData("Requested")] [InlineData("Confirmed")] [InlineData("Refused")] [InlineData("Cancelled")]
    [InlineData("active-buffer")] [InlineData("closed-buffer")]
    public async Task Any_history_locks_changed_terms_but_omitted_and_equal_values_survive(string variant)
    {
        await using var db = await Fixture(); var owner = await TrainingTestData.CreateOwnerAsync(db.Services, true);
        var training = await TrainingTestData.CreateTrainingAsync(db.Services, owner);
        await AddHistory(db, training, variant); var before = await Digest(db);
        Assert.Equal(TrainingOperationStatus.Conflict, (await Management(db).UpdateAsync(owner.Id, new(training.Id, training.Version,
            Fields(training) with { ExternalTotalPriceEuros = 99m, RequiresMotivation = true }))).Status);
        Assert.Equal(before, await Digest(db));
        // Omitted null/false is geen opdracht om voorwaarden te wissen.
        Assert.True((await Management(db).UpdateAsync(owner.Id, new(training.Id, training.Version,
            Fields(training) with { Title = "Metadata zonder voorwaarden", ExternalTotalPriceEuros = null },
            PriceSupplied: false, MotivationSupplied: false))).Succeeded);
        var after = await ReadTraining(db, training.Id);
        Assert.Equal(training.ExternalTotalPriceEuros, after.ExternalTotalPriceEuros);
        Assert.Equal(training.RequiresMotivation, after.RequiresMotivation);
        Assert.True((await Management(db).UpdateAsync(owner.Id, new(after.Id, after.Version, Fields(after)))).Succeeded);
    }

    [Fact]
    public async Task No_history_allows_actual_term_changes()
    {
        await using var db = await Fixture(); var owner = await TrainingTestData.CreateOwnerAsync(db.Services, true);
        var training = await TrainingTestData.CreateTrainingAsync(db.Services, owner);
        Assert.True((await Management(db).UpdateAsync(owner.Id, new(training.Id, training.Version,
            Fields(training) with { ExternalTotalPriceEuros = 0m, RequiresMotivation = true }))).Succeeded);
        var after = await ReadTraining(db, training.Id);
        Assert.Equal(0m, after.ExternalTotalPriceEuros); Assert.True(after.RequiresMotivation);
    }

    [Theory] [InlineData("all")] [InlineData("selected")] [InlineData("invalid")]
    public async Task Audience_linkset_is_atomic_and_never_unassigned_again(string variant)
    {
        await using var db = await Fixture(); var admin = await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training = await TrainingTestData.CreateTrainingAsync(db.Services, await TrainingTestData.CreateOwnerAsync(db.Services));
        var ids = await Departments(db, 2);
        var scope = variant == "all" ? TrainingAudienceScope.AllDepartments : TrainingAudienceScope.SelectedDepartments;
        if (variant == "invalid")
        {
            foreach (var command in new TrainingAudienceCommand[] {
                new(training.Id,training.Version,TrainingAudienceScope.SelectedDepartments,[]),
                new(training.Id,training.Version,TrainingAudienceScope.SelectedDepartments,[ids[0],ids[0]]),
                new(training.Id,training.Version,TrainingAudienceScope.SelectedDepartments,[int.MaxValue]),
                new(training.Id,training.Version,TrainingAudienceScope.AllDepartments,[ids[0]]),
                new(training.Id,training.Version,TrainingAudienceScope.Unassigned,[]),
                new(training.Id,training.Version,(TrainingAudienceScope)99,[]) })
            {
                var before = await Digest(db); Assert.Equal(TrainingOperationStatus.Invalid, (await Management(db).AssignAudienceAsync(admin.Id, command)).Status);
                Assert.Equal(before, await Digest(db));
            }
        }
        else
        {
            Assert.True((await Management(db).AssignAudienceAsync(admin.Id, new(training.Id, training.Version, scope, variant == "all" ? [] : ids))).Succeeded);
            var after = await ReadTraining(db, training.Id);
            Assert.Equal(scope, after.AudienceScope); Assert.NotEqual(training.Version, after.Version);
            Assert.Equal((variant == "all" ? Array.Empty<int>() : ids).Order(), after.TrainingDepartments.Select(x => x.DepartmentId).Order());
            var before = await Digest(db);
            Assert.Equal(TrainingOperationStatus.Invalid, (await Management(db).AssignAudienceAsync(admin.Id,
                new(after.Id,after.Version,TrainingAudienceScope.Unassigned,[]))).Status); Assert.Equal(before,await Digest(db));
        }
    }

    [Fact]
    public async Task Historical_audience_changes_preserve_all_foundation_rows_and_conditions()
    {
        await using var db = await Fixture(); var admin = await TrainingTestData.CreateAdministratorAsync(db.Services);
        var owner = await TrainingTestData.CreateOwnerAsync(db.Services); var training = await TrainingTestData.CreateTrainingAsync(db.Services, owner);
        var moment = await TrainingTestData.CreateMomentAsync(db.Services, training, date: new(2025,1,15));
        foreach (var status in Enum.GetValues<RegistrationStatus>()) await TrainingTestData.CreateRegistrationAsync(db.Services, moment, status);
        await TrainingTestData.CreateWaitlistEntryAsync(db.Services, training); await TrainingTestData.CreateWaitlistEntryAsync(db.Services, training, true);
        var identity = await Digest(db,false); var ids = await Departments(db,1);
        Assert.True((await Management(db).AssignAudienceAsync(admin.Id,new(training.Id,training.Version,TrainingAudienceScope.SelectedDepartments,ids))).Succeeded);
        await using var read=db.CreateScope(); var context=read.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(4,await context.Registrations.CountAsync()); Assert.Equal(2,await context.WaitlistEntries.CountAsync());
        Assert.Equal(moment.Version,(await ReadMoment(db,moment.Id)).Version);
        var after=await ReadTraining(db,training.Id); Assert.Equal(training.ExternalTotalPriceEuros,after.ExternalTotalPriceEuros);
        Assert.Equal(training.RequiresMotivation,after.RequiresMotivation); Assert.Equal(identity,await Digest(db,false));
    }

    [Theory] [InlineData("missing")] [InlineData("empty")] [InlineData("stale")]
    public async Task Direct_service_versions_never_upgrade_automatically(string variant)
    {
        await using var db=await Fixture(); var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));
        var version=Version(variant); var before=await Digest(db);
        Assert.Equal(TrainingOperationStatus.Conflict,(await Management(db).UpdateAsync(admin.Id,new(training.Id,version,Fields(training)))).Status);
        Assert.Equal(TrainingOperationStatus.Conflict,(await Management(db).AssignAudienceAsync(admin.Id,new(training.Id,version,TrainingAudienceScope.AllDepartments,[]))).Status);
        Assert.Equal(before,await Digest(db));
    }

    [Theory] [InlineData("blocked")] [InlineData("roleless")] [InlineData("Pending")] [InlineData("Refused")]
    public async Task Admin_keeps_existing_owner_management_without_identity_repair(string variant)
    {
        await using var db=await Fixture(); var owner=await TrainingTestData.CreateOwnerAsync(db.Services); var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner); await MakeOwnerIneligible(db,owner.Id,variant);
        var before=await Digest(db,false);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Management(db).UpdateAsync(owner.Id,new(training.Id,training.Version,Fields(training)))).Status);
        Assert.True((await Management(db).UpdateAsync(admin.Id,new(training.Id,training.Version,Fields(training) with {Title="Adminbeheer"}))).Succeeded);
        var after=await ReadTraining(db,training.Id);
        Assert.True((await Management(db).AssignAudienceAsync(admin.Id,new(after.Id,after.Version,TrainingAudienceScope.AllDepartments,[]))).Succeeded);
        Assert.Equal(TrainingOperationStatus.Invalid,(await Management(db).CreateAsync(admin.Id,new(Fields(after),owner.Id))).Status);
        Assert.Equal(before,await Digest(db,false));
    }

    [Theory] [InlineData("create-parent")] [InlineData("update-parent")] [InlineData("audience-link")]
    public async Task Actual_late_sql_failure_rolls_back_and_clean_retry_uses_original_version(string point)
    {
        var fault=new TrainingFailureInterceptor(); await using var db=await Fixture(fault);
        var owner=await TrainingTestData.CreateOwnerAsync(db.Services); var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var fields=await TrainingTestData.FieldsAsync(db.Services); var ids=await Departments(db,1);
        var training=point=="create-parent"?null:await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var before=await Digest(db); var oldTimeout=db.Context.Database.GetCommandTimeout();
        var connection=(Microsoft.Data.Sqlite.SqliteConnection)db.Context.Database.GetDbConnection();var oldDefault=connection.DefaultTimeout;var oldState=connection.State;
        Task<TrainingOperationResult> Act()=>training is null?Management(db).CreateAsync(owner.Id,new(fields)):
            point=="update-parent"?Management(db).UpdateAsync(owner.Id,new(training.Id,training.Version,Fields(training) with {Title="Late update"})):
            Management(db).AssignAudienceAsync(admin.Id,new(training.Id,training.Version,TrainingAudienceScope.SelectedDepartments,ids));
        fault.Arm(point); Assert.Equal(TrainingOperationStatus.StorageUnavailable,(await Act()).Status);
        Assert.True(fault.Triggered);Assert.True(fault.SawParentWrite);Assert.True(fault.SawSqlWrite);
        Assert.Equal(before,await Digest(db));Assert.Empty(db.Context.ChangeTracker.Entries());
        Assert.Equal(oldTimeout,db.Context.Database.GetCommandTimeout());Assert.Equal(oldDefault,connection.DefaultTimeout);Assert.Equal(oldState,connection.State);
        fault.Disarm();Assert.True((await Act()).Succeeded);
    }

    [Theory] [InlineData("missing")] [InlineData("Pending")] [InlineData("Refused")] [InlineData("blocked")] [InlineData("roleless")] [InlineData("demo-outside")]
    public async Task Current_actor_denials_preserve_every_stored_value(string variant)
    {
        await using var db=await Fixture(demo:variant!="demo-outside"); var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        var actor=await TestAccountData.CreateAsync(db.Services,RequestedAccountType.InternalInstructor,AccountApprovalStatus.Approved,roles:[RoleNames.Lesgever,RoleNames.Beheerder]);
        await DenyActor(db,actor.Id,variant);var id=variant=="missing"?"ontbrekend":actor.Id;var before=await Digest(db);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Management(db).CreateAsync(id,new(Fields(training),owner.Id))).Status);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Management(db).UpdateAsync(id,new(training.Id,training.Version,Fields(training)))).Status);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Management(db).AssignAudienceAsync(id,new(training.Id,training.Version,TrainingAudienceScope.AllDepartments,[]))).Status);
        Assert.Equal(before,await Digest(db));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Foreign_or_former_creator_instructor_is_not_owner(bool external)
    {
        foreach(var formerAdmin in new[]{false,true})
        {
            await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
            var actor=await TrainingTestData.CreateOwnerAsync(db.Services,external);
            if(formerAdmin) await db.Services.GetRequiredService<UserManager<ApplicationUser>>().AddToRoleAsync(actor,RoleNames.Beheerder);
            var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner,formerAdmin?actor:null);
            if(formerAdmin) await db.Services.GetRequiredService<UserManager<ApplicationUser>>().RemoveFromRoleAsync(await db.Context.Users.SingleAsync(x=>x.Id==actor.Id),RoleNames.Beheerder);
            var before=await Digest(db);
            foreach(var id in new[]{training.Id,int.MaxValue})Assert.Equal(TrainingOperationStatus.NotFound,
                (await Management(db).UpdateAsync(actor.Id,new(id,training.Version,Fields(training)))).Status);
            Assert.Equal(before,await Digest(db));
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Instructor_never_assigns_audience_even_for_own_definition(bool external)
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services,external);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var before=await Digest(db);
        foreach(var id in new[]{training.Id,int.MaxValue})Assert.Equal(TrainingOperationStatus.Forbidden,
            (await Management(db).AssignAudienceAsync(owner.Id,new(id,training.Version,TrainingAudienceScope.AllDepartments,[]))).Status);
        Assert.Equal(before,await Digest(db));
    }

    private static TrainingManagementService Management(FileSqliteTestDatabase db)=>db.Services.GetRequiredService<TrainingManagementService>();
}

/// <summary>Gedeelde onafhankelijke providerbewijzen voor deze service/querytests; geen nieuwe productie-infrastructuur.</summary>
internal static class TrainingServiceTestSupport
{
    public static async Task<FileSqliteTestDatabase> Fixture(TrainingFailureInterceptor? fault=null,bool demo=true)
    {
        var db=await FileSqliteTestDatabase.CreateAsync(interceptor:fault,configureServices:s=>TrainingTestServices.Configure(s,demo));
        await TrainingTestData.InitializeAsync(db.Services);return db;
    }
    public static async Task<string> Digest(FileSqliteTestDatabase db,bool includeFeatures=true)
    {await using var scope=db.CreateScope();return await TrainingTestData.DigestAsync(scope.ServiceProvider,includeFeatures);}
    public static async Task<TrainingEntity> ReadTraining(FileSqliteTestDatabase db,int id)
    {await using var scope=db.CreateScope();return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Trainings.AsNoTracking()
        .Include(x=>x.TrainingDepartments).Include(x=>x.Instructor).SingleAsync(x=>x.Id==id);}
    public static async Task<TrainingMoment> ReadMoment(FileSqliteTestDatabase db,int id)
    {await using var scope=db.CreateScope();return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().TrainingMoments.AsNoTracking()
        .Include(x=>x.Training).Include(x=>x.Location).SingleAsync(x=>x.Id==id);}
    public static async Task<Location> ReadLocation(FileSqliteTestDatabase db,int id)
    {await using var scope=db.CreateScope();return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Locations.AsNoTracking().SingleAsync(x=>x.Id==id);}
    public static Task<int[]> Departments(FileSqliteTestDatabase db,int count)=>db.Context.Departments.AsNoTracking().OrderBy(x=>x.Id).Take(count).Select(x=>x.Id).ToArrayAsync();
    public static TrainingFields Fields(TrainingEntity t)=>new(t.Title,t.Description,t.CategoryId,t.ExternalTotalPriceEuros,t.RequiresMotivation);
    public static TrainingMomentPlanningFields Planning(TrainingMoment m)=>new(m.Date,m.StartTime,m.EndTime,m.LocationId);
    public static Guid? Version(string variant)=>variant=="missing"?null:variant=="empty"?Guid.Empty:Guid.NewGuid();
    public static DateTimeOffset StartUtc(FileSqliteTestDatabase db,TrainingMoment moment)=>db.Services.GetRequiredService<TrainingSchedule>().Validate(moment.Date,moment.StartTime,moment.EndTime).Value!.StartUtc;
    public static DateTimeOffset EndUtc(FileSqliteTestDatabase db,TrainingMoment moment)=>db.Services.GetRequiredService<TrainingSchedule>().Validate(moment.Date,moment.StartTime,moment.EndTime).Value!.EndUtc;
    public static async Task AddHistory(FileSqliteTestDatabase db,TrainingEntity training,string variant)
    {
        if(variant.EndsWith("buffer",StringComparison.Ordinal))await TrainingTestData.CreateWaitlistEntryAsync(db.Services,training,variant=="closed-buffer");
        else await TrainingTestData.CreateRegistrationAsync(db.Services,await TrainingTestData.CreateMomentAsync(db.Services,training),Enum.Parse<RegistrationStatus>(variant));
    }
    public static async Task MakeOwnerIneligible(FileSqliteTestDatabase db,string id,string variant)
    {
        db.Context.ChangeTracker.Clear();var user=await db.Context.Users.SingleAsync(x=>x.Id==id);
        switch(variant)
        {
            case "Pending":user.AccountApprovalStatus=AccountApprovalStatus.Pending;break;
            case "Refused":user.AccountApprovalStatus=AccountApprovalStatus.Refused;break;
            case "blocked":user.IsBlocked=true;break;
            case "roleless":await db.Services.GetRequiredService<UserManager<ApplicationUser>>().RemoveFromRoleAsync(user,RoleNames.Lesgever);break;
            case "missing-profile":db.Context.InternalInstructors.Remove(await db.Context.InternalInstructors.SingleAsync(x=>x.ApplicationUserId==id));break;
            case "opposite-profile":db.Context.InternalInstructors.Remove(await db.Context.InternalInstructors.SingleAsync(x=>x.ApplicationUserId==id));
                db.Context.ExternalInstructors.Add(new(){ApplicationUserId=id,OrganizationName="Fictief tegengesteld"});break;
            case "dual-profile":db.Context.ExternalInstructors.Add(new(){ApplicationUserId=id,OrganizationName="Fictief dubbel"});break;
            case "missing-type":user.RequestedAccountType=null;break;
            case "unknown-type":user.RequestedAccountType=(RequestedAccountType)99;break;
            case "invalid-name":user.FirstName=" \t";break;
            case "department":user.DepartmentId=null;break;
            case "organization":(await db.Context.ExternalInstructors.SingleAsync(x=>x.ApplicationUserId==id)).OrganizationName="\u00a0";break;
        }
        await db.Context.SaveChangesAsync();
    }
    public static async Task DenyActor(FileSqliteTestDatabase db,string id,string variant)
    {
        db.Context.ChangeTracker.Clear();var user=await db.Context.Users.SingleAsync(x=>x.Id==id);
        if(variant=="Pending"||variant=="Refused")user.AccountApprovalStatus=Enum.Parse<AccountApprovalStatus>(variant);
        if(variant=="blocked")user.IsBlocked=true;
        if(variant=="demo-outside")user.DemoSeedKey="fictieve-testdemo";
        if(variant=="roleless"||variant=="revoked")await db.Services.GetRequiredService<UserManager<ApplicationUser>>().RemoveFromRolesAsync(user,[RoleNames.Beheerder,RoleNames.Lesgever]);
        await db.Context.SaveChangesAsync();
    }
}
