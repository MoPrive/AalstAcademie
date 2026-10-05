// Sprint 003: Toetst echte beheerrequests voor zaalcreate/edit/delete, whitelist, CSRF, versie, capaciteit en referenties
// GET-delete toont uitsluitend een bevestiging.
using System.Net;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Web;

/// <summary>40 echte HTTP-cases voor actuele beheerrechten, gedeelde labels, zaalcapaciteit, referenties en deletebevestiging.</summary>
public class LocationManagementHttpTests
{
    // LH1: create/edit volgt PRG; bestaande en historische momenten lezen de actuele gedeelde zaalnaam.
    [Fact] public async Task Create_edit_PRG_and_historical_labels()
    {
        await using var s=await BHttpSession.CreateAsync(admin:true);
        using var create=await Post(s,"Create",Fields("Nieuwe zaal"));Assert.Equal(HttpStatusCode.Redirect,create.StatusCode);
        await s.Run(async sp=>{var db=sp.GetRequiredService<ApplicationDbContext>();var row=await db.Locations.AsNoTracking().SingleAsync(x=>x.Name=="Nieuwe zaal");
            Assert.Equal(24,row.MaximumCapacity);Assert.NotEqual(Guid.Empty,row.Version);
            await TrainingTestData.CreateMomentAsync(sp,s.Training,location:s.Room,date:new DateOnly(2026,9,20));});
        var identity=await s.Digest(false);var f=Fields("Gewijzigde gedeelde zaal");f["Address"]="Gewijzigd adres";f["ExpectedVersion"]=s.Room.Version.ToString();
        using var edit=await Post(s,"Edit",f);Assert.Equal(HttpStatusCode.Redirect,edit.StatusCode);
        var after=await s.ReadLocation();Assert.Equal(f["Name"],after.Name);Assert.Equal(f["Address"],after.Address);Assert.NotEqual(s.Room.Version,after.Version);
        using var details=await s.Browser.GetAsync($"/TrainingManagement/Details/{s.Training.Id}");Assert.Equal(HttpStatusCode.OK,details.StatusCode);
        Assert.Contains(f["Name"],await details.Content.ReadAsStringAsync());BHttpSession.SameMoment(s.Moment!,await s.ReadMoment());
        await s.PreserveTraining();Assert.Equal(identity,await s.Digest(false));
    }
    // LH2: HTML403 op GET en echte CSRF-POST, inclusief oude beheerderclaim na rolverlies.
    [Theory] [InlineData("staff")] [InlineData("internal")] [InlineData("external")] [InlineData("revoked-admin")]
    public async Task Current_role_denial(string variant)
    {
        await using var s=await BHttpSession.CreateAsync(admin:true,moment:false);
        var type=variant=="revoked-admin"?(RequestedAccountType?)null:variant=="external"?RequestedAccountType.ExternalInstructor:
            variant=="internal"?RequestedAccountType.InternalInstructor:RequestedAccountType.Employee;
        var role=variant=="revoked-admin"?RoleNames.Beheerder:variant=="staff"?RoleNames.Medewerker:RoleNames.Lesgever;
        var actor=await s.Factory.AddAccountAsync(type,AccountApprovalStatus.Approved,[role]);using var b=s.Factory.CreateIdentityClient();
        using var login=await b.LoginAsync(actor);Assert.Equal(HttpStatusCode.Redirect,login.StatusCode);
        var token=await b.GetAntiforgeryTokenAsync("/Identity/Account/Login");
        if(variant=="revoked-admin")await s.Change(actor,"roleless",role);
        var before=await s.Digest();
        foreach(var a in new[]{"Index","Create","Edit","Delete"})
        {var path=Path(s,a);using var get=await b.GetAsync(path);Assert.Equal(HttpStatusCode.Forbidden,get.StatusCode);
            if(a=="Index")continue;using var post=await BHttpSession.RawPost(b,path,Form(s,a),token);Assert.Equal(HttpStatusCode.Forbidden,post.StatusCode);}
        Assert.Equal(before,await s.Digest());
    }
    // LH3: bestaande account-/cookiesessiegrenzen blijven ongewijzigd.
    [Theory] [InlineData("anonymous")] [InlineData("Pending")] [InlineData("Refused")] [InlineData("blocked")]
    [InlineData("deleted")] [InlineData("stale")] [InlineData("demo-outside")]
    public Task Session_boundaries(string variant)=>BHttpSession.SessionBoundary(true,variant);
    // LH4: create en edit parsen scalars zonder afronding of stil inkorten.
    [Theory] [InlineData("Name","blank")] [InlineData("Name","201")] [InlineData("Address","501")]
    [InlineData("MaximumCapacity","1.5")] [InlineData("MaximumCapacity","2147483648")] [InlineData("MaximumCapacity","0")]
    public async Task Invalid_fields_are_400(string key,string value)
    {
        await using var s=await BHttpSession.CreateAsync(admin:true,moment:false);
        var raw=value switch{"blank"=>" \t","201"=>new string('a',201),"501"=>new string('b',501),_=>value};
        foreach(var a in new[]{"Create","Edit"})
        {var f=Form(s,a);f[key]=raw;using var r=await Reject(s,a,f,HttpStatusCode.BadRequest);
            Assert.True(WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()).Contains(raw,StringComparison.Ordinal));}
    }
    // LH5: extra velden en dubbele scalars kunnen geen technische/modeldata wijzigen.
    [Fact] public async Task Whitelist_and_multiplicity()
    {
        await using var s=await BHttpSession.CreateAsync(admin:true,moment:false);
        foreach(var a in new[]{"Create","Edit","Delete"})
        {
            var f=Form(s,a);f["Version"]=Guid.NewGuid().ToString();using var r=await Reject(s,a,f,HttpStatusCode.BadRequest);
            var pairs=Form(s,a).ToList();pairs.Add(pairs[0]);pairs.Add(new("__RequestVerificationToken",await s.Token()));
            var before=await s.Digest();using var duplicate=await s.Browser.RawClient.PostAsync(Path(s,a),new FormUrlEncodedContent(pairs));
            Assert.Equal(HttpStatusCode.BadRequest,duplicate.StatusCode);Assert.Equal(before,await s.Digest());
        }
    }
    // LH6: invalidwire400/stale409 op edit en delete; oude tokens blijven zichtbaar.
    [Theory] [InlineData("missing")] [InlineData("blank")] [InlineData("malformed")] [InlineData("empty-guid")] [InlineData("stale")]
    public async Task Version_is_strict_for_edit_and_delete(string variant)
    {
        await using var s=await BHttpSession.CreateAsync(admin:true,moment:false);
        if(variant=="stale")await s.Run(async sp=>Assert.True((await sp.GetRequiredService<LocationManagementService>().UpdateAsync(s.Admin.UserId,
            new(s.Room.Id,s.Room.Version,new("Onafhankelijk gewijzigd",null,24)))).Succeeded));
        var version=variant switch{"missing"=>null,"blank"=>"","malformed"=>"geen-guid","empty-guid"=>Guid.Empty.ToString(),_=>s.Room.Version.ToString()};
        foreach(var a in new[]{"Edit","Delete"})
        {var f=Form(s,a);if(version is null)f.Remove("ExpectedVersion");else f["ExpectedVersion"]=version;
            using var r=await Reject(s,a,f,variant=="stale"?HttpStatusCode.Conflict:HttpStatusCode.BadRequest);
            BHttpSession.Value(await r.Content.ReadAsStringAsync(),"ExpectedVersion",version??"");}
    }
    // LH7: gepland maximum blijft beschermd tot het volledige einde; verleden/Cancelled blokkeert niet.
    [Theory] [InlineData("before-start")] [InlineData("running")] [InlineData("exact-end")] [InlineData("past-cancelled")]
    public async Task Planned_maximum_boundary(string variant)
    {
        foreach(var cancelled in variant=="past-cancelled"?new[]{false,true}:new[]{false})
        {
            await using var s=await BHttpSession.CreateAsync(admin:true,today:true,cancelled:cancelled);
            await s.Run(sp=>{((FixedTimeProvider)sp.GetRequiredService<TimeProvider>()).UtcNow=variant switch{
                "running"=>new DateTimeOffset(2026,10,2,12,5,0,TimeSpan.Zero),
                "exact-end"=>new DateTimeOffset(2026,10,2,12,10,0,TimeSpan.Zero),
                "past-cancelled"=>new DateTimeOffset(2026,10,2,12,cancelled?0:10,cancelled?0:1,TimeSpan.Zero),
                _=>new DateTimeOffset(2026,10,2,12,0,0,TimeSpan.Zero)};return Task.CompletedTask;});
            var f=Form(s,"Edit");f["MaximumCapacity"]="8";
            if(variant is "before-start" or "running"){using var denied=await Reject(s,"Edit",f,HttpStatusCode.Conflict);}
            else {using var saved=await Post(s,"Edit",f);Assert.Equal(HttpStatusCode.Redirect,saved.StatusCode);Assert.Equal(8,(await s.ReadLocation()).MaximumCapacity);}
            BHttpSession.SameMoment(s.Moment!,await s.ReadMoment());
        }
    }
    // LH8: zaalverhoging verandert geen maximum of momentversie.
    [Fact] public async Task Capacity_increase_preserves_moments()
    {await using var s=await BHttpSession.CreateAsync(admin:true);var f=Form(s,"Edit");f["MaximumCapacity"]="30";
        using var r=await Post(s,"Edit",f);Assert.Equal(HttpStatusCode.Redirect,r.StatusCode);Assert.Equal(30,(await s.ReadLocation()).MaximumCapacity);BHttpSession.SameMoment(s.Moment!,await s.ReadMoment());}
    // LH9: ongebruikte delete-GET is readonly, daadwerkelijke verwijdering alleen met bevestigde CSRF-POST.
    [Fact] public async Task Unused_delete_confirmation_and_PRG()
    {
        await using var s=await BHttpSession.CreateAsync(admin:true,moment:false);var before=await s.Digest();
        using var get=await s.Browser.GetAsync(Path(s,"Delete"));Assert.Equal(HttpStatusCode.OK,get.StatusCode);
        Assert.Contains("Zaal verwijderen bevestigen",await get.Content.ReadAsStringAsync());Assert.Equal(before,await s.Digest());
        var identity=await s.Digest(false);using var post=await Post(s,"Delete",Form(s,"Delete"));Assert.Equal(HttpStatusCode.Redirect,post.StatusCode);
        await s.Run(async sp=>Assert.False(await sp.GetRequiredService<ApplicationDbContext>().Locations.AnyAsync(x=>x.Id==s.Room.Id)));
        await s.PreserveTraining();Assert.Equal(identity,await s.Digest(false));
    }
    // LH10: iedere referentie verhindert fysieke delete, ook past/Cancelled; GET409 zonder submit.
    [Theory] [InlineData("future")] [InlineData("past")] [InlineData("Cancelled")]
    public async Task Referenced_room_is_never_deletable(string variant)
    {
        await using var s=await BHttpSession.CreateAsync(admin:true,moment:false);
        await s.Run(async sp=>await TrainingTestData.CreateMomentAsync(sp,s.Training,location:s.Room,date:variant=="past"?new DateOnly(2026,9,20):null,
            status:variant=="Cancelled"?TrainingMomentStatus.Cancelled:TrainingMomentStatus.Scheduled));
        var before=await s.Digest();using var get=await s.Browser.GetAsync(Path(s,"Delete"));Assert.Equal(HttpStatusCode.Conflict,get.StatusCode);
        Assert.DoesNotContain("Zaal verwijderen bevestigen",await get.Content.ReadAsStringAsync());Assert.Equal(before,await s.Digest());
        using var post=await Reject(s,"Delete",Form(s,"Delete"),HttpStatusCode.Conflict);
    }
    // LH11: alle writes vereisen CSRF.
    [Theory] [InlineData("Create")] [InlineData("Edit")] [InlineData("Delete")]
    public async Task CSRF_required(string action)
    {await using var s=await BHttpSession.CreateAsync(admin:true,moment:false);var before=await s.Digest();
        using var r=await s.Browser.RawClient.PostAsync(Path(s,action),new FormUrlEncodedContent(Form(s,action)));
        Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode);Assert.Equal(before,await s.Digest());}
    // LH12: geëncodeerde rawinvoer, oude versie en expliciete herleeslink, zonder ModelState-clear.
    [Fact] public async Task Encoded_input_and_original_version()
    {
        await using var s=await BHttpSession.CreateAsync(admin:true,moment:false);var f=Form(s,"Edit");
        f["Name"]="<script>alert('zaal')</script>";f["Address"]="<b>Fictief adres</b>";f["MaximumCapacity"]="1.5";
        using var r=await Reject(s,"Edit",f,HttpStatusCode.BadRequest);var html=await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain(f["Name"],html);Assert.DoesNotContain(f["Address"],html);
        BHttpSession.Value(html,"Name",f["Name"]);BHttpSession.Value(html,"MaximumCapacity","1.5");BHttpSession.Value(html,"ExpectedVersion",s.Room.Version.ToString());
        Assert.Contains("role=\"alert\"",html);Assert.Contains($"href=\"{Path(s,"Edit")}\"",html);
    }
    // LH13: SQL heeft echt geschreven vóór de fout; rollback en bewuste retry behouden het oorspronkelijke token.
    [Fact] public async Task SQL_503_edit_delete_matrix()
    {
        foreach(var a in new[]{"Edit","Delete"})
        {
            var fault=new TrainingFailureInterceptor();await using var s=await BHttpSession.CreateAsync(admin:true,moment:false,fault:fault);
            var f=Form(s,a);var before=await s.Digest();fault.Arm(a=="Edit"?"location-update":"location-delete");
            using var r=await Post(s,a,f);Assert.Equal(HttpStatusCode.ServiceUnavailable,r.StatusCode);
            Assert.True(fault.Triggered);Assert.True(fault.SawSqlWrite);Assert.Equal(before,await s.Digest());
            var html=await r.Content.ReadAsStringAsync();BHttpSession.Value(html,"ExpectedVersion",s.Room.Version.ToString());Assert.DoesNotContain("SQL",html);
            fault.Disarm();using var retry=await Post(s,a,f);Assert.Equal(HttpStatusCode.Redirect,retry.StatusCode);
            if(a=="Edit")Assert.NotEqual(s.Room.Version,(await s.ReadLocation()).Version);
            else await s.Run(async sp=>Assert.False(await sp.GetRequiredService<ApplicationDbContext>().Locations.AnyAsync(x=>x.Id==s.Room.Id)));
        }
    }
    // LH14: navigatie gebruikt actuele DB-rechten naast de serverpolicy.
    [Fact] public async Task Navigation_rechecks_current_admin_rights()
    {
        await using var s=await BHttpSession.CreateAsync(admin:true,moment:false);
        using var before=await s.Browser.GetAsync("/");Assert.Contains("href=\"/Locations\"",await before.Content.ReadAsStringAsync());
        await s.Change(s.Actor,"roleless",RoleNames.Beheerder);
        using var after=await s.Browser.GetAsync("/");Assert.Equal(HttpStatusCode.OK,after.StatusCode);Assert.DoesNotContain("href=\"/Locations\"",await after.Content.ReadAsStringAsync());
        using var denied=await s.Browser.GetAsync("/Locations");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
    }
    // LH15: scope/read bestaat vóór inputvalidatie; ontbrekende ID404 zonder write.
    [Fact] public async Task Missing_location_404_before_version_parsing()
    {
        await using var s=await BHttpSession.CreateAsync(admin:true,moment:false);var before=await s.Digest();
        foreach(var a in new[]{"Edit","Delete"})
        {var path=$"/Locations/{a}/2147483647";using var get=await s.Browser.GetAsync(path);Assert.Equal(HttpStatusCode.NotFound,get.StatusCode);
            using var post=await s.Browser.PostFormAsync(path,new Dictionary<string,string>{{"ExpectedVersion","invalid"}},"/Identity/Account/Login");
            Assert.Equal(HttpStatusCode.NotFound,post.StatusCode);}
        Assert.Equal(before,await s.Digest());
    }
    private static Dictionary<string,string> Fields(string name="Gewijzigde zaal")=>new(){["Name"]=name,["Address"]="Fictief adres",["MaximumCapacity"]="24"};
    private static Dictionary<string,string> Form(BHttpSession s,string a)
    {var f=a=="Delete"?new Dictionary<string,string>():Fields();if(a!="Create")f["ExpectedVersion"]=s.Room.Version.ToString();return f;}
    private static string Path(BHttpSession s,string a)=>a switch{"Index"=>"/Locations","Create"=>"/Locations/Create",_=>$"/Locations/{a}/{s.Room.Id}"};
    private static Task<HttpResponseMessage> Post(BHttpSession s,string a,Dictionary<string,string> f)=>s.Browser.PostFormAsync(Path(s,a),f,"/Identity/Account/Login");
    private static async Task<HttpResponseMessage> Reject(BHttpSession s,string a,Dictionary<string,string> f,HttpStatusCode status)
    {var before=await s.Digest();var r=await Post(s,a,f);Assert.Equal(status,r.StatusCode);Assert.Equal(before,await s.Digest());return r;}
}

