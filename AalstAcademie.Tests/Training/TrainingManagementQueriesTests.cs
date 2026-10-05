// Sprint 003: Toetst huidige eigenaarsscope, gedeelde metadata, historieflags, filters en stabiele paginering
// Een lesgever krijgt geen opleiding van een andere eigenaar te zien.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Training;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AalstAcademie.Tests.Training.TrainingServiceTestSupport;

namespace AalstAcademie.Tests.Training;

/// <summary>Zes querygroepen bewijzen vaste gebruikersscope, actuele metadata, historieflags en stabiele pagina's.</summary>
public sealed class TrainingManagementQueriesTests
{
    [Fact]
    public async Task Internal_and_external_instructors_read_only_their_own_definitions()
    {
        await using var db=await Fixture();var internalOwner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var externalOwner=await TrainingTestData.CreateOwnerAsync(db.Services,true);
        var first=await TrainingTestData.CreateTrainingAsync(db.Services,internalOwner);var second=await TrainingTestData.CreateTrainingAsync(db.Services,externalOwner);
        foreach(var (owner,own,foreign) in new[]{(internalOwner,first,second),(externalOwner,second,first)})
        {
            var page=await Query(db).GetListAsync(owner.Id,new());Assert.Equal(own.Id,Assert.Single(page.Value!.Rows).Id);
            Assert.True((await Query(db).GetDetailsAsync(owner.Id,own.Id)).Succeeded);
            Assert.Equal(TrainingOperationStatus.NotFound,(await Query(db).GetDetailsAsync(owner.Id,foreign.Id)).Status);
        }
    }

    [Fact]
    public async Task Foreign_missing_and_unapproved_actor_reads_do_not_leak_or_write()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var foreign=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));var before=await Digest(db);
        foreach(var id in new[]{foreign.Id,int.MaxValue})
        {
            var result=await Query(db).GetDetailsAsync(owner.Id,id);Assert.Equal(TrainingOperationStatus.NotFound,result.Status);Assert.Null(result.Value);
        }
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Query(db).GetDetailsAsync("ontbrekend",foreign.Id)).Status);Assert.Equal(before,await Digest(db));
    }

    [Fact]
    public async Task Admin_without_instructor_profile_reads_all_and_current_eligible_references()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var external=await TrainingTestData.CreateOwnerAsync(db.Services,true);
        var first=await TrainingTestData.CreateTrainingAsync(db.Services,owner,admin);var second=await TrainingTestData.CreateTrainingAsync(db.Services,external,admin);
        Assert.Equal(new[]{first.Id,second.Id}.Order(),(await Query(db).GetListAsync(admin.Id,new())).Value!.Rows.Select(x=>x.Id).Order());
        Assert.True((await Query(db).GetDetailsAsync(admin.Id,first.Id)).Succeeded);
        Assert.Equal(new[]{owner.Id,external.Id}.Order(),(await Query(db).GetEligibleOwnersAsync(admin.Id)).Value!.Select(x=>x.UserId).Order());
        Assert.False(await db.Context.InternalInstructors.AnyAsync(x=>x.ApplicationUserId==admin.Id));Assert.False(await db.Context.ExternalInstructors.AnyAsync(x=>x.ApplicationUserId==admin.Id));
        Assert.NotEmpty((await Query(db).GetReferencesAsync(owner.Id)).Value!.Categories);
        Assert.NotEmpty((await Query(db).GetReferencesAsync(owner.Id)).Value!.Departments);
        Assert.Equal(TrainingOperationStatus.Forbidden,(await Query(db).GetCategoriesAsync(owner.Id)).Status);
        Assert.True((await Query(db).GetCategoriesAsync(admin.Id)).Succeeded);
    }

    [Fact]
    public async Task Details_show_current_names_categories_terms_audience_and_real_history_flags()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);
        var internalOwner=await TrainingTestData.CreateOwnerAsync(db.Services);var externalOwner=await TrainingTestData.CreateOwnerAsync(db.Services,true);
        var ids=await Departments(db,1);
        var first=await TrainingTestData.CreateTrainingAsync(db.Services,internalOwner,admin,TrainingAudienceScope.SelectedDepartments,ids);
        var second=await TrainingTestData.CreateTrainingAsync(db.Services,externalOwner,admin,TrainingAudienceScope.AllDepartments);
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,first,date:new(2025,1,15));
        await TrainingTestData.CreateRegistrationAsync(db.Services,moment,RegistrationStatus.Cancelled);
        db.Context.ChangeTracker.Clear();var user=await db.Context.Users.SingleAsync(x=>x.Id==internalOwner.Id);user.FirstName="Actuele";user.LastName="Naam";
        (await db.Context.ExternalInstructors.SingleAsync(x=>x.ApplicationUserId==externalOwner.Id)).OrganizationName="Huidige organisatie";
        var category=await db.Context.Categories.SingleAsync(x=>x.Id==first.CategoryId);category.Name="Huidige categorie";
        (await db.Context.Trainings.SingleAsync(x=>x.Id==first.Id)).Description="Regel\n<script>gewone tekst</script>";
        await db.Context.SaveChangesAsync();var before=await Digest(db);
        var details=(await Query(db).GetDetailsAsync(admin.Id,first.Id)).Value!;var external=(await Query(db).GetDetailsAsync(admin.Id,second.Id)).Value!;
        Assert.Equal("Actuele Naam",details.Training.OwnerDisplayName);Assert.Equal("Huidige categorie",details.Training.CategoryName);
        Assert.Equal(first.InstructorUserId,details.Training.OwnerUserId);Assert.Null(details.Training.ExternalTotalPriceEuros);
        Assert.Equal("Huidige organisatie",external.Training.OwnerDisplayName);Assert.True(external.Training.IsExternalOwner);
        Assert.Equal(second.ExternalTotalPriceEuros,external.Training.ExternalTotalPriceEuros);
        Assert.Equal(TrainingAudienceScope.SelectedDepartments,details.Training.AudienceScope);Assert.Equal(ids[0],Assert.Single(details.Departments).Id);
        Assert.Equal("Regel\n<script>gewone tekst</script>",details.Description);Assert.True(details.CanEdit);Assert.True(details.CanAssignAudience);
        Assert.True(details.HasHistory);Assert.False(details.CanChangeConditions);Assert.Equal(moment.Id,Assert.Single(details.Moments).Id);
        Assert.True(details.CanCreateMoment);Assert.False(external.HasHistory);Assert.True(external.CanChangeConditions);Assert.Equal(before,await Digest(db));
    }

    [Fact]
    public async Task Category_audience_filters_and_title_id_order_keep_twenty_row_pages_stable()
    {
        await using var db=await Fixture();var admin=await TrainingTestData.CreateAdministratorAsync(db.Services);var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var category=await TrainingTestData.CreateCategoryAsync(db.Services,"Filtercategorie");var id=(await Departments(db,1))[0];
        for(var index=0;index<25;index++)await TrainingTestData.CreateTrainingAsync(db.Services,owner,admin,(TrainingAudienceScope)(index%3),
            index%3==2?[id]:null);
        db.Context.ChangeTracker.Clear();var records=await db.Context.Trainings.OrderBy(x=>x.Id).ToListAsync();
        foreach(var (row,index) in records.Select((row,index)=>(row,index))){row.Title=index%2==0?"A titel":"B titel";row.CategoryId=category.Id;}
        await db.Context.SaveChangesAsync();
        var first=(await Query(db).GetListAsync(owner.Id,new())).Value!;var second=(await Query(db).GetListAsync(owner.Id,new(Page:2))).Value!;
        Assert.Equal(20,first.Rows.Count);Assert.Equal(5,second.Rows.Count);Assert.Equal(25,first.TotalCount);Assert.Equal(2,first.TotalPages);
        Assert.Equal(records.OrderBy(x=>x.Title,StringComparer.Ordinal).ThenBy(x=>x.Id).Select(x=>x.Id),first.Rows.Concat(second.Rows).Select(x=>x.Id));
        var selected=(await Query(db).GetListAsync(admin.Id,new(TrainingAudienceFilter.SelectedDepartments,category.Id))).Value!;
        Assert.Equal(records.Where(x=>x.AudienceScope==TrainingAudienceScope.SelectedDepartments).OrderBy(x=>x.Title,StringComparer.Ordinal).ThenBy(x=>x.Id).Select(x=>x.Id),selected.Rows.Select(x=>x.Id));
        foreach(var filter in new TrainingListFilter[]{new((TrainingAudienceFilter)99),new(Page:0),new(CategoryId:-1),new(CategoryId:int.MaxValue)})
            Assert.Equal(TrainingOperationStatus.Invalid,(await Query(db).GetListAsync(owner.Id,filter)).Status);
    }

    [Fact]
    public async Task Empty_and_out_of_range_pages_do_not_invent_categories_or_offers()
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);var before=await Digest(db);
        Assert.Empty((await Query(db).GetReferencesAsync(owner.Id)).Value!.Categories);
        foreach(var page in new[]{1,999})
        {
            var result=await Query(db).GetListAsync(owner.Id,new(Page:page));Assert.True(result.Succeeded);Assert.Empty(result.Value!.Rows);
            Assert.Equal(0,result.Value.TotalCount);Assert.False(result.Value.HasCategories);
        }
        Assert.Equal(before,await Digest(db));
    }
    private static TrainingManagementQueries Query(FileSqliteTestDatabase db)=>db.Services.GetRequiredService<TrainingManagementQueries>();
}
