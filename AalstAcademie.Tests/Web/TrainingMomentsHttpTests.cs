// Sprint 003: Gebruikt echte cookies, CSRF en SQLite-readbacks voor momentplanning, capaciteit en de GET/POST-zaalkeuze zonder JavaScript
// Verouderde keuzes krijgen conflict met behoud van invoer.
using System.Net;
using System.Text.RegularExpressions;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Controllers;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Tests.Web;

/// <summary>72 HTTP-cases met echte cookies/CSRF, actuele guards en onafhankelijke volledige opslagreadbacks.</summary>
public class TrainingMomentsHttpTests
{
    // MH1: vaste eigenaar, planning, maximum en doelgroep worden onafhankelijk teruggelezen.
    [Theory] [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task Create_PRG_roundtrips(bool external, bool admin)
    {
        await using var s = await BHttpSession.CreateAsync(admin, external, moment:false);
        var identity = await s.Digest(false);
        using var r = await s.Post("Create", s.Form("Create")); Assert.Equal(HttpStatusCode.Redirect,r.StatusCode);
        var row = await s.SingleMoment(); Assert.Equal(s.Training.Id,row.TrainingId); Assert.Equal(s.Room.Id,row.LocationId);
        Assert.Equal(new DateOnly(2026,12,15),row.Date); Assert.Equal(new TimeOnly(14,0),row.StartTime); Assert.Equal(new TimeOnly(16,0),row.EndTime);
        Assert.Equal(12,row.MaximumParticipants); Assert.Equal(TrainingMomentStatus.Scheduled,row.Status); Assert.NotEqual(Guid.Empty,row.Version);
        Assert.Equal($"/TrainingManagement/Details/{s.Training.Id}",r.Headers.Location?.OriginalString);
        await s.PreserveTraining(); Assert.Equal(identity,await s.Digest(false));
    }
    // MH2: echte historische rijen blijven bij de oorspronkelijke definitie/uitvoering.
    [Fact] public async Task New_ID_preserves_history_and_audience()
    {
        await using var s = await BHttpSession.CreateAsync();
        await s.Run(async sp => { await TrainingTestData.CreateRegistrationAsync(sp,s.Moment!,RegistrationStatus.Refused);
            await TrainingTestData.CreateWaitlistEntryAsync(sp,s.Training,true);
            Assert.True((await sp.GetRequiredService<TrainingManagementService>().AssignAudienceAsync(s.Admin.UserId,
                new(s.Training.Id,s.Training.Version,TrainingAudienceScope.AllDepartments,[]))).Succeeded); });
        var old=await s.ReadMoment(); var f=s.Form("Create"); f["StartTime"]="16:00";f["EndTime"]="18:00";
        using var r=await s.Post("Create",f); Assert.Equal(HttpStatusCode.Redirect,r.StatusCode);
        await s.Run(async sp => { var db=sp.GetRequiredService<ApplicationDbContext>(); Assert.Equal(2,await db.TrainingMoments.CountAsync());
            Assert.Equal(old.Id,(await db.Registrations.AsNoTracking().SingleAsync()).TrainingMomentId);
            Assert.NotNull((await db.WaitlistEntries.AsNoTracking().SingleAsync()).ClosedAtUtc);
            Assert.Equal(TrainingAudienceScope.AllDepartments,(await db.Trainings.AsNoTracking().SingleAsync()).AudienceScope); });
        BHttpSession.SameMoment(old,await s.ReadMoment());
    }
    // MH3: bestaande accountgrenzen worden op alle nieuwe GET/POSTs toegepast.
    [Theory] [InlineData("anonymous")] [InlineData("Pending")] [InlineData("Refused")] [InlineData("blocked")]
    [InlineData("deleted")] [InlineData("stale")] [InlineData("demo-outside")]
    public Task Sessions_deny_without_writes(string variant)=>BHttpSession.SessionBoundary(false,variant);
    // MH4: rol403 en scoped404 komen vóór ongeldige invoer/details.
    [Fact] public async Task Staff_foreign_and_missing_scope()
    {
        await using var s=await BHttpSession.CreateAsync();
        foreach(var staff in new[]{true,false})
        {
            var actor=await s.Factory.AddAccountAsync(staff?RequestedAccountType.Employee:RequestedAccountType.InternalInstructor,
                AccountApprovalStatus.Approved,[staff?RoleNames.Medewerker:RoleNames.Lesgever]);
            using var b=s.Factory.CreateIdentityClient();using var login=await b.LoginAsync(actor);Assert.Equal(HttpStatusCode.Redirect,login.StatusCode);
            var before=await s.Digest();
            foreach(var a in new[]{"Create","Edit","Capacity"})
            { using var get=await b.GetAsync(s.Path(a));Assert.Equal(staff?HttpStatusCode.Forbidden:HttpStatusCode.NotFound,get.StatusCode);
              using var post=await b.PostFormAsync(s.Path(a),new Dictionary<string,string>{{"Date","invalid"}},"/Identity/Account/Login");
              Assert.Equal(staff?HttpStatusCode.Forbidden:HttpStatusCode.NotFound,post.StatusCode); }
            using var availability=await b.GetAsync($"/TrainingMoments/Availability?trainingId={s.Training.Id}&Date=invalid");
            Assert.Equal(staff?HttpStatusCode.Forbidden:HttpStatusCode.NotFound,availability.StatusCode);Assert.Equal(before,await s.Digest());
        }
        var digest=await s.Digest();using var missing=await s.Browser.PostFormAsync("/TrainingMoments/Edit/2147483647",
            new Dictionary<string,string>{{"ExpectedVersion","invalid"}},"/Identity/Account/Login");
        Assert.Equal(HttpStatusCode.NotFound,missing.StatusCode);Assert.Equal(digest,await s.Digest());
    }
    // MH5: een open formulier verleent geen blijvende rechten.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Revoked_role_after_GET(bool admin)
    {
        await using var s=await BHttpSession.CreateAsync(admin);var token=await s.Token(s.Path("Edit"));
        await s.Change(s.Actor,"roleless",admin?RoleNames.Beheerder:RoleNames.Lesgever);
        var before=await s.Digest();
        foreach(var a in new[]{"Create","Edit","Capacity"}) {using var get=await s.Browser.GetAsync(s.Path(a));Assert.Equal(HttpStatusCode.Forbidden,get.StatusCode);
            using var post=await BHttpSession.RawPost(s.Browser,s.Path(a),s.Form(a),token);Assert.Equal(HttpStatusCode.Forbidden,post.StatusCode);}
        Assert.Equal(before,await s.Digest());
    }
    // MH6/7: nieuwe momentaanmaak vereist actuele ownereligibility, bestaand adminbeheer alleen passende owner-shape.
    [Theory] [InlineData("blocked")] [InlineData("roleless")] [InlineData("Pending")] [InlineData("profileloss")]
    public async Task Fixed_owner_rechecked_for_create(string v)
    {await using var s=await BHttpSession.CreateAsync(admin:true,moment:false);var token=await s.Token(s.Path("Create"));await s.Change(s.Owner,v);
        var before=await s.Digest();using var r=await BHttpSession.RawPost(s.Browser,s.Path("Create"),s.Form("Create"),token);
        Assert.Equal(HttpStatusCode.Conflict,r.StatusCode);Assert.Equal(before,await s.Digest());}
    [Theory] [InlineData("blocked")] [InlineData("roleless")]
    public async Task Existing_admin_edit_with_ineligible_owner(string v)
    {await using var s=await BHttpSession.CreateAsync(admin:true);await s.Change(s.Owner,v);var f=s.Form("Edit");f["StartTime"]="14:00:00.0000001";
        using var r=await s.Post("Edit",f);Assert.Equal(HttpStatusCode.Redirect,r.StatusCode);var row=await s.ReadMoment();
        Assert.Equal(new TimeOnly(14,0).Ticks+1,row.StartTime.Ticks);Assert.Equal(12,row.MaximumParticipants);await s.PreserveTraining();}
    // MH8: zowel onbekende velden als dubbele scalars worden afgewezen.
    [Theory] [InlineData("Create")] [InlineData("Edit")] [InlineData("Capacity")]
    public async Task Whitelist_and_multiplicity(string a)
    {
        await using var s=await BHttpSession.CreateAsync();
        foreach(var key in new[]{"Status","TrainingId","ActorUserId","InstructorUserId",a=="Capacity"?"Date":a=="Edit"?"MaximumParticipants":"ExpectedVersion"})
        {var f=s.Form(a);f[key]="1";using var r=await s.Rejected(a,f,HttpStatusCode.BadRequest);}
        var pairs=s.Form(a).ToList();pairs.Add(pairs[0]);pairs.Add(new("__RequestVerificationToken",await s.Token()));
        var before=await s.Digest();using var response=await s.Browser.RawClient.PostAsync(s.Path(a),new FormUrlEncodedContent(pairs));
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(before,await s.Digest());
    }
    // MH9: geen afronding/fallback bij tekstuele scalars of onbekende zaal-ID.
    [Theory] [InlineData("Date","2026-02-30")] [InlineData("StartTime","14:00:00.12345678")]
    [InlineData("LocationId","1.5")] [InlineData("LocationId","2147483647")] [InlineData("MaximumParticipants","1.5")]
    [InlineData("MaximumParticipants","2147483648")] [InlineData("MaximumParticipants","0")]
    public async Task Invalid_scalars(string key,string raw)
    {await using var s=await BHttpSession.CreateAsync(moment:false);var f=s.Form("Create");f[key]=raw;
        using var r=await s.Rejected("Create",f,HttpStatusCode.BadRequest);Assert.True(WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()).Contains(raw,StringComparison.Ordinal));}
    // MH10: begin/einde op niet-bestaande of dubbel voorkomende Belgische uren, op beide endpoints.
    [Theory] [InlineData("2026-03-29")] [InlineData("2026-10-25")]
    public async Task DST_endpoint_matrix(string date)
    {await using var s=await BHttpSession.CreateAsync();foreach(var a in new[]{"Create","Edit"})foreach(var start in new[]{true,false})
        {var f=s.Form(a);f["Date"]=date;f["StartTime"]=start?"02:30":"01:30";f["EndTime"]=start?"03:30":"02:30";
         using var r=await s.Rejected(a,f,HttpStatusCode.BadRequest);}}
    // MH11/12: halfopen overlap tegenover toegestane aansluitingen en andere dagen.
    [Theory] [InlineData("14:30","15:00")] [InlineData("13:00","17:00")] [InlineData("13:00","15:00")] [InlineData("15:00","17:00")]
    public async Task Overlap_conflict(string begin,string end)
    {await using var s=await BHttpSession.CreateAsync();var f=s.Form("Create");f["StartTime"]=begin;f["EndTime"]=end;
        using var r=await s.Rejected("Create",f,HttpStatusCode.Conflict);}
    [Theory] [InlineData("12:00","14:00","2026-12-15")] [InlineData("16:00","18:00","2026-12-15")] [InlineData("14:00","16:00","2026-12-16")]
    public async Task Adjacent_or_other_day(string begin,string end,string date)
    {await using var s=await BHttpSession.CreateAsync();var f=s.Form("Create");f["StartTime"]=begin;f["EndTime"]=end;f["Date"]=date;
        using var r=await s.Post("Create",f);Assert.Equal(HttpStatusCode.Redirect,r.StatusCode);
        await s.Run(async sp=>Assert.Equal(2,await sp.GetRequiredService<ApplicationDbContext>().TrainingMoments.CountAsync()));}
    // MH13: Cancelled-rij blijft bewaard maar boekt geen zaal.
    [Fact] public async Task Cancelled_does_not_book()
    {await using var s=await BHttpSession.CreateAsync(cancelled:true);var old=await s.ReadMoment();
        using var r=await s.Post("Create",s.Form("Create"));Assert.Equal(HttpStatusCode.Redirect,r.StatusCode);BHttpSession.SameMoment(old,await s.ReadMoment());}
    // MH14: eigen ID is uitgesloten, volledige ticks en maximum blijven intact.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Planning_edit_ticks_and_maximum(bool admin)
    {await using var s=await BHttpSession.CreateAsync(admin);var f=s.Form("Edit");f["StartTime"]="14:00:00.1234567";f["EndTime"]="16:00:00.7654321";
        using var r=await s.Post("Edit",f);Assert.Equal(HttpStatusCode.Redirect,r.StatusCode);var row=await s.ReadMoment();
        Assert.Equal(new TimeOnly(14,0).Ticks+1234567,row.StartTime.Ticks);Assert.Equal(new TimeOnly(16,0).Ticks+7654321,row.EndTime.Ticks);
        Assert.Equal(12,row.MaximumParticipants);Assert.Equal(s.Room.Id,row.LocationId);Assert.NotEqual(s.Moment!.Version,row.Version);await s.PreserveTraining();
        using var get=await s.Browser.GetAsync(s.Path("Edit"));var html=await get.Content.ReadAsStringAsync();BHttpSession.Value(html,"StartTime",f["StartTime"]);BHttpSession.Value(html,"EndTime",f["EndTime"]);}
    // MH15: elke echte registratiehistorie na GET blokkeert planning.
    [Theory] [InlineData(RegistrationStatus.Requested)] [InlineData(RegistrationStatus.Confirmed)] [InlineData(RegistrationStatus.Refused)] [InlineData(RegistrationStatus.Cancelled)]
    public async Task History_added_after_GET(RegistrationStatus status)
    {await using var s=await BHttpSession.CreateAsync();var token=await s.Token(s.Path("Edit"));
        await s.Run(async sp=>await TrainingTestData.CreateRegistrationAsync(sp,s.Moment!,status));var before=await s.Digest();
        using var r=await BHttpSession.RawPost(s.Browser,s.Path("Edit"),s.Form("Edit"),token);Assert.Equal(HttpStatusCode.Conflict,r.StatusCode);Assert.Equal(before,await s.Digest());
        using var get=await s.Browser.GetAsync(s.Path("Edit"));Assert.DoesNotContain("Planning opslaan",await get.Content.ReadAsStringAsync());}
    // MH16: korte klokstappen houden cookies geldig; geposte toekomstige datum omzeilt opgeslagen start niet.
    [Theory] [InlineData("equal")] [InlineData("after")] [InlineData("cross-get")]
    public async Task Stored_start_boundary(string variant)
    {await using var s=await BHttpSession.CreateAsync(today:true);
        // Alleen cross-get leest een nog wijzigbaar formulier; equal/after lezen eerst de readonly toestand.
        var token=variant=="cross-get"?await s.Token(s.Path("Edit")):await s.Token();
        await s.Run(sp=>{((FixedTimeProvider)sp.GetRequiredService<TimeProvider>()).UtcNow=new DateTimeOffset(2026,10,2,12,1,variant=="equal"?0:1,TimeSpan.Zero);return Task.CompletedTask;});
        if(variant!="cross-get") { using var read=await s.Browser.GetAsync(s.Path("Edit"));
            Assert.Equal(HttpStatusCode.OK,read.StatusCode);Assert.DoesNotContain("Planning opslaan",await read.Content.ReadAsStringAsync()); }
        var before=await s.Digest();foreach(var a in new[]{"Edit","Capacity"}){var f=s.Form(a);if(a=="Edit")f["Date"]="2026-12-20";
            using var r=await BHttpSession.RawPost(s.Browser,s.Path(a),f,token);Assert.Equal(HttpStatusCode.Conflict,r.StatusCode);}Assert.Equal(before,await s.Digest());}
    // MH17: een kleinere zaal mag geen stil maximumverlies veroorzaken.
    [Fact] public async Task Smaller_room_conflict()
    {await using var s=await BHttpSession.CreateAsync(maximum:16);Location? small=null;await s.Run(async sp=>small=await TrainingTestData.CreateLocationAsync(sp,"Kleine zaal",12));
        var f=s.Form("Edit");f["LocationId"]=small!.Id.ToString();using var r=await s.Rejected("Edit",f,HttpStatusCode.Conflict);}
    // MH18: read-beschikbaarheid geeft geen reservering; actuele booking/cap worden bij write herlezen.
    [Theory] [InlineData("booking")] [InlineData("capacity")]
    public async Task Stale_availability(string variant)
    {await using var s=await BHttpSession.CreateAsync(moment:false);var token=await s.Token(s.Availability());
        await s.Run(async sp=>{if(variant=="booking")await TrainingTestData.CreateMomentAsync(sp,s.Training,location:s.Room);
            else Assert.True((await sp.GetRequiredService<LocationManagementService>().UpdateAsync(s.Admin.UserId,new(s.Room.Id,s.Room.Version,new(s.Room.Name,null,8)))).Succeeded);});
        var before=await s.Digest();using var r=await BHttpSession.RawPost(s.Browser,s.Path("Create"),s.Form("Create"),token);
        Assert.Equal(HttpStatusCode.Conflict,r.StatusCode);Assert.Equal(before,await s.Digest());Assert.Contains("niet beschikbaar",await r.Content.ReadAsStringAsync());}
    // MH19: capacitywrite bewaart alle planning, Training en Identity.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Capacity_preserves_planning(bool admin)
    {await using var s=await BHttpSession.CreateAsync(admin);var identity=await s.Digest(false);var f=s.Form("Capacity");f["MaximumParticipants"]="18";
        using var r=await s.Post("Capacity",f);Assert.Equal(HttpStatusCode.Redirect,r.StatusCode);var row=await s.ReadMoment();BHttpSession.SamePlanning(s.Moment!,row);
        Assert.Equal(18,row.MaximumParticipants);Assert.NotEqual(s.Moment!.Version,row.Version);await s.PreserveTraining();Assert.Equal(identity,await s.Digest(false));}
    // MH20: Requested+Confirmed van dit moment zijn het minimum, andere statussen/uitvoeringen niet.
    [Fact] public async Task Actual_occupancy_per_moment()
    {await using var s=await BHttpSession.CreateAsync();await s.Run(async sp=>{foreach(var status in Enum.GetValues<RegistrationStatus>())await TrainingTestData.CreateRegistrationAsync(sp,s.Moment!,status);
        var other=await TrainingTestData.CreateMomentAsync(sp,s.Training,location:s.Room,date:new DateOnly(2026,12,16));await TrainingTestData.CreateRegistrationAsync(sp,other);
        await TrainingTestData.CreateRegistrationAsync(sp,other,RegistrationStatus.Confirmed);});
        using var get=await s.Browser.GetAsync(s.Path("Capacity"));Assert.Contains("Minimaal 2",await get.Content.ReadAsStringAsync());
        var f=s.Form("Capacity");f["MaximumParticipants"]="2";using var r=await s.Post("Capacity",f);Assert.Equal(HttpStatusCode.Redirect,r.StatusCode);Assert.Equal(2,(await s.ReadMoment()).MaximumParticipants);
        await s.Run(async sp=>Assert.Equal(6,await sp.GetRequiredService<ApplicationDbContext>().Registrations.CountAsync()));}
    // MH21: actieve bezetting en zaalcap zijn businessconflicten.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Capacity_bounds(bool above)
    {await using var s=await BHttpSession.CreateAsync();await s.Run(async sp=>{await TrainingTestData.CreateRegistrationAsync(sp,s.Moment!);await TrainingTestData.CreateRegistrationAsync(sp,s.Moment!,RegistrationStatus.Confirmed);});
        var f=s.Form("Capacity");f["MaximumParticipants"]=above?"25":"1";using var r=await s.Rejected("Capacity",f,HttpStatusCode.Conflict);}
    // MH22: vier onbruikbare tokens400 tegenover echte stale409 op edit en capacity.
    [Theory] [InlineData("missing")] [InlineData("blank")] [InlineData("malformed")] [InlineData("empty-guid")] [InlineData("stale")]
    public async Task Wire_version(string variant)
    {await using var s=await BHttpSession.CreateAsync();if(variant=="stale")await s.Run(async sp=>Assert.True((await sp.GetRequiredService<TrainingMomentManagementService>().ChangeCapacityAsync(s.Actor.UserId,new(s.Moment!.Id,s.Moment.Version,13))).Succeeded));
        var v=variant switch{"missing"=>null,"blank"=>"","malformed"=>"geen-guid","empty-guid"=>Guid.Empty.ToString(),_=>s.Moment!.Version.ToString()};
        foreach(var a in new[]{"Edit","Capacity"}){var f=s.Form(a);if(v is null)f.Remove("ExpectedVersion");else f["ExpectedVersion"]=v;
            using var r=await s.Rejected(a,f,variant=="stale"?HttpStatusCode.Conflict:HttpStatusCode.BadRequest);BHttpSession.Value(await r.Content.ReadAsStringAsync(),"ExpectedVersion",v??"");}}
    // MH23: geldige cookie is zonder CSRF onvoldoende.
    [Theory] [InlineData("Create")] [InlineData("Edit")] [InlineData("Capacity")]
    public async Task CSRF_required(string a)
    {await using var s=await BHttpSession.CreateAsync();var before=await s.Digest();using var r=await s.Browser.RawClient.PostAsync(s.Path(a),new FormUrlEncodedContent(s.Form(a)));
        Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);Assert.Equal(before,await s.Digest());}
    // MH24: veilige rawinput en oorspronkelijke versie, focusbare samenvatting en expliciete reload.
    [Fact] public async Task Encoding_and_original_input()
    {await using var s=await BHttpSession.CreateAsync();var f=s.Form("Edit");f["Date"]="<script>alert('datum')</script>";
        using var r=await s.Rejected("Edit",f,HttpStatusCode.BadRequest);var html=await r.Content.ReadAsStringAsync();Assert.DoesNotContain(f["Date"],html);
        BHttpSession.Value(html,"Date",f["Date"]);BHttpSession.Value(html,"ExpectedVersion",s.Moment!.Version.ToString());Assert.Contains("role=\"alert\"",html);Assert.Contains($"href=\"{s.Path("Edit")}\"",html);}
    // MH25: echte SQL-write vóór fault, volledige rollback en bewuste retry met oorspronkelijke token.
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SQL_503_with_readback_and_retry(bool edits)
    {foreach(var a in edits?new[]{"Edit","Capacity"}:new[]{"Create"}){var fault=new TrainingFailureInterceptor();
        await using var s=await BHttpSession.CreateAsync(moment:edits,fault:fault);var f=s.Form(a);if(a=="Edit")f["EndTime"]="17:00";if(a=="Capacity")f["MaximumParticipants"]="18";
        var before=await s.Digest();fault.Arm(a=="Create"?"moment-create":a=="Edit"?"moment-planning":"moment-capacity");
        using var r=await s.Post(a,f);Assert.Equal(HttpStatusCode.ServiceUnavailable,r.StatusCode);Assert.True(fault.Triggered);Assert.True(fault.SawSqlWrite);Assert.Equal(before,await s.Digest());
        var html=await r.Content.ReadAsStringAsync();Assert.DoesNotContain("SQL",html);if(a!="Create")BHttpSession.Value(html,"ExpectedVersion",s.Moment!.Version.ToString());
        fault.Disarm();using var retry=await s.Post(a,f);Assert.Equal(HttpStatusCode.Redirect,retry.StatusCode);
        if(a=="Create")Assert.Equal(12,(await s.SingleMoment()).MaximumParticipants);else Assert.NotEqual(s.Moment!.Version,(await s.ReadMoment()).Version);}}
    // MH26: geannuleerd is readonly; ook rechtstreekse write bewaart alles.
    [Fact] public async Task Cancelled_readonly()
    {await using var s=await BHttpSession.CreateAsync(cancelled:true);foreach(var a in new[]{"Edit","Capacity"}){using var get=await s.Browser.GetAsync(s.Path(a));
        // De gedeelde logoutform blijft geldig; uitsluitend deze momentwrite ontbreekt.
        Assert.Equal(HttpStatusCode.OK,get.StatusCode);Assert.DoesNotContain(a=="Edit"?"Planning opslaan":"Maximum opslaan",await get.Content.ReadAsStringAsync());
        using var post=await s.Rejected(a,s.Form(a),HttpStatusCode.Conflict);}}
    // MH27: Delete/Restore blijven afwezig. Sprint 005 voegt uitsluitend adminannulering toe;
    // de historische testnaam blijft behouden en de lesgever mag ook met geldige CSRF niets annuleren.
    [Fact] public async Task No_cancel_delete_restore_endpoint()
    {await using var s=await BHttpSession.CreateAsync();var before=await s.Digest();foreach(var a in new[]{"Delete","Restore"})
        {var p=$"/TrainingMoments/{a}/{s.Moment!.Id}";using var get=await s.Browser.GetAsync(p);Assert.Equal(HttpStatusCode.NotFound,get.StatusCode);
         using var post=await s.Browser.PostFormAsync(p,s.Form("Capacity"),"/Identity/Account/Login");Assert.Contains(post.StatusCode,new[]{HttpStatusCode.NotFound,HttpStatusCode.MethodNotAllowed});
         using var delete=await s.Browser.RawClient.DeleteAsync(p);Assert.Contains(delete.StatusCode,new[]{HttpStatusCode.NotFound,HttpStatusCode.MethodNotAllowed});}
        var actions=s.Factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items.OfType<ControllerActionDescriptor>().Where(x=>x.ControllerTypeInfo.AsType()==typeof(TrainingMomentsController)).ToArray();
        Assert.NotEmpty(actions);Assert.DoesNotContain(actions,x=>new[]{"Delete","Restore"}.Contains(x.ActionName));
        var cancelPath=$"/TrainingMoments/Cancel/{s.Moment!.Id}";
        using var cancelGet=await s.Browser.GetAsync(cancelPath);Assert.Equal(HttpStatusCode.Forbidden,cancelGet.StatusCode);
        using var cancelPost=await s.Browser.PostFormAsync(cancelPath,s.Form("Capacity"),"/Identity/Account/Login");Assert.Equal(HttpStatusCode.Forbidden,cancelPost.StatusCode);
        using var cancelDelete=await s.Browser.RawClient.DeleteAsync(cancelPath);Assert.Contains(cancelDelete.StatusCode,new[]{HttpStatusCode.NotFound,HttpStatusCode.MethodNotAllowed});
        var cancelActions=actions.Where(x=>x.ActionName=="Cancel").ToArray();Assert.Equal(2,cancelActions.Length);
        Assert.Equal(new[]{"GET","POST"},cancelActions.SelectMany(x=>x.ActionConstraints!.OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>().SelectMany(c=>c.HttpMethods)).Order().ToArray());
        Assert.All(cancelActions,x=>Assert.Contains(x.MethodInfo.GetCustomAttributes(true).OfType<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(),a=>a.Policy==AalstAcademie.Web.Security.AccountPolicies.ApprovedAdministrator));
        Assert.True(cancelActions.Single(x=>x.ActionConstraints!.OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>().Any(c=>c.HttpMethods.Contains("POST"))).MethodInfo.IsDefined(typeof(Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute),true));
        Assert.Equal(before,await s.Digest());}
    // MH28: scoped GET is readonly, geen privédata/claim; aparte echte CSRF-POST werkt zonder script.
    [Fact] public async Task No_JS_availability_scope_and_roundtrip()
    {await using var s=await BHttpSession.CreateAsync();var before=await s.Digest();
        using var get=await s.Browser.GetAsync(s.Availability(true));Assert.Equal(HttpStatusCode.OK,get.StatusCode);var html=await get.Content.ReadAsStringAsync();
        Assert.Contains("reserveert geen zaal",html);Assert.DoesNotContain("Fictieve motivatie",html);Assert.DoesNotContain("OccupiedCount",html);Assert.DoesNotContain(s.Owner.Email,html);
        Assert.Equal(before,await s.Digest());int foreign=0;
        await s.Run(async sp=>{var owner=await TrainingTestData.CreateOwnerAsync(sp);var training=await TrainingTestData.CreateTrainingAsync(sp,owner);
            foreign=(await TrainingTestData.CreateMomentAsync(sp,training,date:new DateOnly(2026,12,17))).Id;});
        using var denied=await s.Browser.GetAsync($"/TrainingMoments/Availability?trainingId={s.Training.Id}&momentId={foreign}&Date=invalid");Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
        await using var fresh=await BHttpSession.CreateAsync(moment:false);var digest=await fresh.Digest();
        using var lookup=await fresh.Browser.GetAsync(fresh.Availability());Assert.Equal(HttpStatusCode.OK,lookup.StatusCode);var read=await lookup.Content.ReadAsStringAsync();
        BHttpSession.Value(read,"Date","2026-12-15");Assert.Contains("__RequestVerificationToken",read);Assert.Equal(digest,await fresh.Digest());
        var token=await fresh.Token(fresh.Availability());
        using var saved=await BHttpSession.RawPost(fresh.Browser,fresh.Path("Create"),fresh.Form("Create"),token);
        Assert.Equal(HttpStatusCode.Redirect,saved.StatusCode);Assert.Equal(fresh.Room.Id,(await fresh.SingleMoment()).LocationId);}
}

/// <summary>Alleen testsetup: één eigen host/SQLite-doel, echte Identity, onafhankelijke scopes en hash-only foutbewijs.</summary>
internal sealed class BHttpSession : IAsyncDisposable
{
    public AccountWebApplicationFactory Factory {get;private init;}=null!;
    public IdentityHttpClient Browser {get;private init;}=null!;
    public TestIdentity Owner {get;private init;}=null!;
    public TestIdentity Admin {get;private init;}=null!;
    public TestIdentity Actor {get;private init;}=null!;
    public TrainingEntity Training {get;private set;}=null!;
    public Location Room {get;private set;}=null!;
    public TrainingMoment? Moment {get;private set;}
    public static async Task<BHttpSession> CreateAsync(bool admin=false,bool external=false,bool moment=true,bool cancelled=false,
        bool today=false,int maximum=12,TrainingFailureInterceptor? fault=null)
    {
        var factory=new AccountWebApplicationFactory();
        if(fault is not null)factory.ConfigureServicesBeforeStart=services=>{
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options=>options.UseSqlite(factory.ConnectionString).AddInterceptors(fault));};
        var owner=await factory.AddAccountAsync(external?RequestedAccountType.ExternalInstructor:RequestedAccountType.InternalInstructor,AccountApprovalStatus.Approved,[RoleNames.Lesgever]);
        var administrator=await factory.AddAccountAsync(null,AccountApprovalStatus.Approved,[RoleNames.Beheerder]);var actor=admin?administrator:owner;
        var browser=factory.CreateIdentityClient();using var login=await browser.LoginAsync(actor);Assert.Equal(HttpStatusCode.Redirect,login.StatusCode);
        var s=new BHttpSession{Factory=factory,Browser=browser,Owner=owner,Admin=administrator,Actor=actor};
        await s.Run(async sp=>{var user=await sp.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(x=>x.Id==owner.UserId);
            s.Training=await TrainingTestData.CreateTrainingAsync(sp,user);s.Room=await TrainingTestData.CreateLocationAsync(sp);
            if(moment)s.Moment=await TrainingTestData.CreateMomentAsync(sp,s.Training,location:s.Room,date:today?new DateOnly(2026,10,2):null,
                start:today?new TimeOnly(14,1):null,end:today?new TimeOnly(14,10):null,maximumParticipants:maximum,status:cancelled?TrainingMomentStatus.Cancelled:TrainingMomentStatus.Scheduled);});
        return s;
    }
    public Task Run(Func<IServiceProvider,Task> action)=>Factory.WithServicesAsync(action);
    public string Path(string action)=>action=="Create"?$"/TrainingMoments/Create/{Training.Id}":$"/TrainingMoments/{action}/{Moment!.Id}";
    public Dictionary<string,string> Form(string a)=>a switch{
        "Create"=>new(){["Date"]="2026-12-15",["StartTime"]="14:00",["EndTime"]="16:00",["LocationId"]=Room.Id.ToString(),["MaximumParticipants"]="12"},
        "Edit"=>new(){["Date"]=Moment!.Date.ToString("yyyy-MM-dd"),["StartTime"]=Moment.StartTime.ToString("HH:mm:ss.fffffff"),["EndTime"]=Moment.EndTime.ToString("HH:mm:ss.fffffff"),["LocationId"]=Moment.LocationId.ToString(),["ExpectedVersion"]=Moment.Version.ToString()},
        _=>new(){["MaximumParticipants"]="12",["ExpectedVersion"]=Moment!.Version.ToString()}};
    public string Availability(bool edit=false)
    {var f=Form(edit?"Edit":"Create");f["trainingId"]=Training.Id.ToString();if(edit)f["momentId"]=Moment!.Id.ToString();
        return "/TrainingMoments/Availability?"+string.Join("&",f.Select(x=>Uri.EscapeDataString(x.Key)+"="+Uri.EscapeDataString(x.Value)));}
    public Task<string> Token(string path="/Identity/Account/Login")=>Browser.GetAntiforgeryTokenAsync(path);
    public Task<HttpResponseMessage> Post(string a,Dictionary<string,string> f)=>Browser.PostFormAsync(Path(a),f,"/Identity/Account/Login");
    public async Task<HttpResponseMessage> Rejected(string a,Dictionary<string,string> f,HttpStatusCode expected)
    {var before=await Digest();var r=await Post(a,f);Assert.Equal(expected,r.StatusCode);Assert.Equal(before,await Digest());return r;}
    public async Task<string> Digest(bool features=true)
    {string value="";await Run(async sp=>value=await TrainingTestData.DigestAsync(sp,features));return value;}
    public async Task<TrainingMoment> ReadMoment(int? id=null)
    {TrainingMoment? row=null;await Run(async sp=>row=await sp.GetRequiredService<ApplicationDbContext>().TrainingMoments.AsNoTracking().Include(x=>x.Location).Include(x=>x.Training).SingleAsync(x=>x.Id==(id??Moment!.Id)));return row!;}
    public async Task<TrainingMoment> SingleMoment()
    {int id=0;await Run(async sp=>id=await sp.GetRequiredService<ApplicationDbContext>().TrainingMoments.Select(x=>x.Id).SingleAsync());return await ReadMoment(id);}
    public async Task<Location> ReadLocation()
    {Location? row=null;await Run(async sp=>row=await sp.GetRequiredService<ApplicationDbContext>().Locations.AsNoTracking().SingleAsync(x=>x.Id==Room.Id));return row!;}
    public async Task PreserveTraining()
    {await Run(async sp=>{var row=await sp.GetRequiredService<ApplicationDbContext>().Trainings.AsNoTracking().SingleAsync(x=>x.Id==Training.Id);
        Assert.Equal(Training.Version,row.Version);Assert.Equal(Training.InstructorUserId,row.InstructorUserId);Assert.Equal(Training.AudienceScope,row.AudienceScope);
        Assert.Equal(Training.ExternalTotalPriceEuros,row.ExternalTotalPriceEuros);Assert.Equal(Training.RequiresMotivation,row.RequiresMotivation);Assert.Equal(Training.Title,row.Title);});}
    public static void SamePlanning(TrainingMoment a,TrainingMoment b)
    {Assert.Equal(a.Id,b.Id);Assert.Equal(a.TrainingId,b.TrainingId);Assert.Equal(a.LocationId,b.LocationId);Assert.Equal(a.Date,b.Date);Assert.Equal(a.StartTime,b.StartTime);Assert.Equal(a.EndTime,b.EndTime);Assert.Equal(a.Status,b.Status);}
    public static void SameMoment(TrainingMoment a,TrainingMoment b){SamePlanning(a,b);Assert.Equal(a.MaximumParticipants,b.MaximumParticipants);Assert.Equal(a.Version,b.Version);}
    public async Task Change(TestIdentity identity,string v,string role=RoleNames.Lesgever)
    {await Run(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();var user=await db.Users.SingleAsync(x=>x.Id==identity.UserId);
        if(v=="blocked")user.IsBlocked=true;else if(v=="Pending")user.AccountApprovalStatus=AccountApprovalStatus.Pending;
        else if(v=="profileloss")db.InternalInstructors.Remove(await db.InternalInstructors.SingleAsync(x=>x.ApplicationUserId==identity.UserId));
        else {var stamp=user.SecurityStamp;Assert.True((await sp.GetRequiredService<UserManager<ApplicationUser>>().RemoveFromRoleAsync(user,role)).Succeeded);Assert.Equal(stamp,user.SecurityStamp);}
        await db.SaveChangesAsync();});}
    public static Task<HttpResponseMessage> RawPost(IdentityHttpClient b,string path,Dictionary<string,string> f,string token)=>
        b.RawClient.PostAsync(path,new FormUrlEncodedContent(f.Concat(new[]{new KeyValuePair<string,string>("__RequestVerificationToken",token)})));
    public static void Value(string html,string name,string expected)
    {foreach(Match input in Regex.Matches(html,"(?is)<input\\b[^>]*>")){var key=Regex.Match(input.Value,"(?is)\\bname\\s*=\\s*([\"'])(.*?)\\1");
        if(WebUtility.HtmlDecode(key.Groups[2].Value)!=name)continue;var value=Regex.Match(input.Value,"(?is)\\bvalue\\s*=\\s*([\"'])(.*?)\\1");
        Assert.Equal(expected,WebUtility.HtmlDecode(value.Groups[2].Value));return;}Assert.Fail("Verwacht formuliercontrol ontbreekt: "+name);}
    /// <summary>Een admin zonder Training-FK wordt na login gewijzigd/verwijderd; geen nieuwe host of same-DB-reset.</summary>
    public static async Task SessionBoundary(bool locations,string variant)
    {
        await using var factory=new AccountWebApplicationFactory(demoEnabled:variant!="demo-outside");
        var actor=await factory.AddAccountAsync(null,AccountApprovalStatus.Approved,[RoleNames.Beheerder],emailConfirmed:true);
        var owner=await factory.AddAccountAsync(RequestedAccountType.InternalInstructor,AccountApprovalStatus.Approved,[RoleNames.Lesgever],emailConfirmed:true);
        using var b=factory.CreateIdentityClient();if(variant!="anonymous"){using var login=await b.LoginAsync(actor);Assert.Equal(HttpStatusCode.Redirect,login.StatusCode);}
        var token=await b.GetAntiforgeryTokenAsync("/Identity/Account/Login");TrainingEntity? training=null;Location? room=null;TrainingMoment? moment=null;
        await factory.WithServicesAsync(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();
            training=await TrainingTestData.CreateTrainingAsync(sp,await db.Users.AsNoTracking().SingleAsync(x=>x.Id==owner.UserId));
            room=await TrainingTestData.CreateLocationAsync(sp);moment=await TrainingTestData.CreateMomentAsync(sp,training,location:room);
            if(variant=="anonymous")return;var user=await db.Users.SingleAsync(x=>x.Id==actor.UserId);
            if(variant is "Pending" or "Refused")user.AccountApprovalStatus=Enum.Parse<AccountApprovalStatus>(variant);
            else if(variant=="blocked")user.IsBlocked=true;else if(variant=="deleted")db.Users.Remove(user);
            else if(variant=="stale")Assert.True((await sp.GetRequiredService<UserManager<ApplicationUser>>().UpdateSecurityStampAsync(user)).Succeeded);
            else user.DemoSeedKey="fictieve-http-demo-buiten-grens";await db.SaveChangesAsync();});
        async Task<string> Digest(){string v="";await factory.WithServicesAsync(async sp=>v=await TrainingTestData.DigestAsync(sp));return v;}
        var before=await Digest();var paths=locations?new[]{"/Locations/Create",$"/Locations/Edit/{room!.Id}",$"/Locations/Delete/{room.Id}"}:
            new[]{$"/TrainingMoments/Create/{training!.Id}",$"/TrainingMoments/Edit/{moment!.Id}",$"/TrainingMoments/Capacity/{moment.Id}"};
        foreach(var path in paths){using var get=await b.GetAsync(path);Denial(get,variant);
            using var post=await RawPost(b,path,new(){["ExpectedVersion"]=(locations?room!.Version:moment!.Version).ToString()},token);Denial(post,variant);}
        Assert.Equal(before,await Digest());
    }
    private static void Denial(HttpResponseMessage r,string v){Assert.Equal(HttpStatusCode.Redirect,r.StatusCode);
        if(v is "Pending" or "Refused")Assert.Equal("/Account/Status",r.Headers.Location?.OriginalString);else Assert.Contains("/Identity/Account/Login",r.Headers.Location?.OriginalString??"");}
    public async ValueTask DisposeAsync(){Browser.Dispose();await Factory.DisposeAsync();}
}

