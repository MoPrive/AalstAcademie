// Sprint 003: Controleert de exacte aanbodset en Belgische kalenderdatum, gereserveerde sleutelbotsingen en rollback na echte SQL
// Sprint 004: de volledige bootstrap voegt een aparte deelnamebatch toe; de oorspronkelijke vijf momenten blijven afzonderlijk gecontroleerd.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Tests.Data;

/// <summary>Zes cases bewijzen de volledige afgesproken aanbodset, Belgische kalender en transactionele afwijzing/rollback.</summary>
public class DemoOfferSeederTests
{
    [Fact] public async Task Exact_complete_set_and_relations_with_empty_foundations()
    {
        await using var environment=new DemoStartupTestEnvironment();await using var provider=environment.CreateServices();
        await using var scope=provider.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>().InitializeAsync();
        await using var read=provider.CreateAsyncScope();var db=read.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(new[]{300101,300102,300103},await db.Categories.OrderBy(x=>x.Id).Select(x=>x.Id).ToArrayAsync());
        Assert.Equal(new[]{24,12,8},await db.Locations.OrderBy(x=>x.Id).Select(x=>x.MaximumCapacity).ToArrayAsync());
        Assert.Equal(new[]{300301,300302,300303,300304},await db.Trainings.OrderBy(x=>x.Id).Select(x=>x.Id).ToArrayAsync());
        // De historische testnaam blijft herkenbaar; het volledige bootstrapbeeld heeft nu ook de004-deelnamebatch.
        Assert.Equal(7,await db.TrainingMoments.CountAsync());Assert.Equal(4,await db.Registrations.CountAsync());Assert.Equal(2,await db.WaitlistEntries.CountAsync());
        var definitions=await db.Trainings.AsNoTracking().OrderBy(x=>x.Id).ToListAsync();
        Assert.Equal("Excel basis",definitions[0].Title);Assert.Null(definitions[0].ExternalTotalPriceEuros);
        Assert.Equal(DemoAccountCatalog.InternalInstructorId,definitions[0].InstructorUserId);Assert.Equal(TrainingAudienceScope.AllDepartments,definitions[0].AudienceScope);
        Assert.Equal(450.00m,definitions[1].ExternalTotalPriceEuros);Assert.True(definitions[1].RequiresMotivation);
        Assert.Equal(DemoAccountCatalog.ExternalInstructorId,definitions[1].InstructorUserId);Assert.Equal(TrainingAudienceScope.SelectedDepartments,definitions[1].AudienceScope);
        Assert.Equal(TrainingAudienceScope.Unassigned,definitions[2].AudienceScope);Assert.Equal(TrainingAudienceScope.AllDepartments,definitions[3].AudienceScope);
        var link=await db.TrainingDepartments.AsNoTracking().Include(x=>x.Department).SingleAsync();Assert.Equal(300302,link.TrainingId);Assert.Equal("ICT",link.Department.Name);
        var category=await db.Categories.SingleAsync(x=>x.Id==300101);Assert.Equal("DIGITALE VAARDIGHEDEN",db.Entry(category).Property<string>("NormalizedName").CurrentValue);
        Assert.NotEqual(Guid.Empty,db.Entry(category).Property<Guid>("Version").CurrentValue);
        var moments=await db.TrainingMoments.AsNoTracking().Include(x=>x.Location).Where(x=>x.Id<300406).OrderBy(x=>x.Id).ToListAsync();
        Assert.Equal(5,moments.Count);
        Assert.All(moments,m=>{Assert.Equal(TrainingMomentStatus.Scheduled,m.Status);Assert.True(m.MaximumParticipants<=m.Location.MaximumCapacity);Assert.NotEqual(Guid.Empty,m.Version);});
        Assert.Equal(moments[0].EndTime,moments[1].StartTime);Assert.Equal(moments[0].Date,moments[1].Date);
        Assert.Equal(new[]{16,16,10,12,12},moments.Select(x=>x.MaximumParticipants).ToArray());
        Assert.False(await db.TrainingMoments.AnyAsync(x=>x.LocationId==300203));
        Assert.True(provider.GetRequiredService<DemoCredentials>().IsReady);
    }
    [Theory] [InlineData("2026-01-10T12:00:00Z","2026-01-10")] [InlineData("2026-07-10T12:00:00Z","2026-07-10")]
    [InlineData("2026-10-01T23:30:00Z","2026-10-02")]
    public async Task Dates_use_Belgian_calendar_in_winter_summer_and_UTC_boundary(string utc,string belgianDate)
    {
        await using var environment=new DemoStartupTestEnvironment();environment.Clock.UtcNow=DateTimeOffset.Parse(utc);
        await using var provider=environment.CreateServices();await using var scope=provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>().InitializeAsync();
        await using var read=provider.CreateAsyncScope();var moments=await read.ServiceProvider.GetRequiredService<ApplicationDbContext>().TrainingMoments.AsNoTracking().Where(x=>x.Id<300406).OrderBy(x=>x.Id).ToListAsync();
        Assert.Equal(5,moments.Count);
        var today=DateOnly.Parse(belgianDate);Assert.Equal(new[]{7,7,8,9,-7},moments.Select(x=>x.Date.DayNumber-today.DayNumber).ToArray());
        Assert.Equal(today.AddDays(10),(await read.ServiceProvider.GetRequiredService<ApplicationDbContext>().TrainingMoments.SingleAsync(x=>x.Id==300406)).Date);
        Assert.Equal(new TimeOnly(9,0),moments[0].StartTime);Assert.Equal(new TimeOnly(13,0),moments[1].EndTime);
    }
    [Fact] public async Task Late_SQL_moment_failure_rolls_back_whole_offer_batch_without_ready_hint()
    {
        await using var environment=new DemoStartupTestEnvironment();var fault=new DemoStartupFailureInterceptor();
        await using var provider=environment.CreateServices(interceptor:fault);await using var scope=provider.CreateAsyncScope();
        await PrepareAccounts(scope.ServiceProvider);var before=await Digest(provider);fault.Arm("offer");
        await Assert.ThrowsAnyAsync<Exception>(()=>Seeder(scope.ServiceProvider).SeedAsync());
        Assert.True(fault.Triggered);Assert.True(fault.SawSqlWrite);Assert.Equal(before,await Digest(provider));
        Assert.False(provider.GetRequiredService<DemoCredentials>().IsReady);
        await using var read=provider.CreateAsyncScope();var db=read.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(6,await db.Users.CountAsync());Assert.Equal(0,await db.Categories.CountAsync());Assert.Equal(0,await db.TrainingMoments.CountAsync());
    }
    // Collisions worden niet geadopteerd; een volledige onafhankelijke digest bewijst geen halve seed.
    [Fact] public async Task Reserved_category_location_training_and_moment_collisions_preserve_existing_rows()
    {
        foreach(var point in new[]{"category","category-name","location","training","moment"})
        {
            await using var environment=new DemoStartupTestEnvironment();await using var provider=environment.CreateServices();
            await using var scope=provider.CreateAsyncScope();await PrepareAccounts(scope.ServiceProvider);
            var db=scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if(point is "category" or "category-name")
            {
                var category=new Category{Id=point=="category"?300101:1,Name=point=="category"?"Bestaande reservering":"Digitale vaardigheden"};
                db.Categories.Add(category);db.Entry(category).Property<string>("NormalizedName").CurrentValue=TrainingValueValidation.ValidateCategoryName(category.Name).NormalizedName!;
                db.Entry(category).Property<Guid>("Version").CurrentValue=Guid.NewGuid();
            }
            else
            {
                var room=new Location{Id=point=="location"?300201:1,Name="Bewaarde zaal",MaximumCapacity=24,Version=Guid.NewGuid()};db.Locations.Add(room);
                if(point is "training" or "moment")
                {
                    var category=new Category{Id=1,Name="Bewaarde categorie"};db.Categories.Add(category);db.Entry(category).Property<string>("NormalizedName").CurrentValue="BEWAARDE CATEGORIE";
                    db.Entry(category).Property<Guid>("Version").CurrentValue=Guid.NewGuid();
                    var training=new TrainingEntity{Id=point=="training"?300301:1,Title="Bewaarde definitie",CategoryId=1,
                        InstructorUserId=DemoAccountCatalog.InternalInstructorId,AudienceScope=TrainingAudienceScope.Unassigned,Version=Guid.NewGuid()};
                    db.Trainings.Add(training);
                    if(point=="moment")db.TrainingMoments.Add(new TrainingMoment{Id=300401,TrainingId=1,LocationId=1,Date=new DateOnly(2026,12,15),
                        StartTime=new TimeOnly(14,0),EndTime=new TimeOnly(16,0),MaximumParticipants=12,Status=TrainingMomentStatus.Scheduled,Version=Guid.NewGuid()});
                }
            }
            await db.SaveChangesAsync();var before=await Digest(provider);
            await Assert.ThrowsAnyAsync<Exception>(()=>Seeder(scope.ServiceProvider).SeedAsync());Assert.Equal(before,await Digest(provider));
            Assert.False(provider.GetRequiredService<DemoCredentials>().IsReady);
        }
    }
    private static DemoOfferSeeder Seeder(IServiceProvider sp)=>new(sp.GetRequiredService<ApplicationDbContext>(),sp.GetRequiredService<DemoMode>(),
        sp.GetRequiredService<TimeProvider>(),sp.GetRequiredService<TrainingSchedule>());
    private static async Task PrepareAccounts(IServiceProvider sp){await sp.GetRequiredService<DatabaseInitializer>().InitializeAsync();Assert.True((await sp.GetRequiredService<DemoDataSeeder>().SeedAsync()).IsSeeded);}
    private static async Task<string> Digest(ServiceProvider provider){await using var scope=provider.CreateAsyncScope();return await TrainingTestData.DigestAsync(scope.ServiceProvider);}
}

