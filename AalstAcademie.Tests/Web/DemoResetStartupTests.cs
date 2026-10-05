// Sprint 003: Start echte Program-hosts op eigen databases voor volledige reset, non-demo-behoud, leases, oude cookies en falende startup
// Dit activeert nooit reset op de gebruikersdatabase.
using System.Net;
using System.Security.Cryptography;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Web;

/// <summary>Acht echte Program-startupcases, uitsluitend op een eigen tijdelijk doel en keyring.</summary>
public class DemoResetStartupTests
{
    // Alle vorige sessiedata verdwijnen; de nieuwe host levert weer de afgesproken complete seed.
    [Fact]
    public async Task Two_demo_hosts_remove_previous_session_rows()
    {
        await using var e=new DemoStartupTestEnvironment();
        await using(var first=new DemoStartupWebApplicationFactory(e))
        {
            using var browser=first.CreateIdentityClient();await Ready(first,browser);
            await first.WithServicesAsync(async sp=>{
                var db=sp.GetRequiredService<ApplicationDbContext>();
                var applicant=await TestAccountData.CreateAsync(sp,RequestedAccountType.Employee);
                await TrainingTestData.CreateRegistrationAsync(sp,await db.TrainingMoments.SingleAsync(x=>x.Id==300401),applicant:applicant);
                await TrainingTestData.CreateWaitlistEntryAsync(sp,await db.Trainings.SingleAsync(x=>x.Id==300301),applicant:applicant);
                await TrainingTestData.CreateCategoryAsync(sp,"Vorige sessie");
                (await db.Trainings.SingleAsync(x=>x.Id==300301)).Title="Vorige sessietitel";await db.SaveChangesAsync();
            });
            Assert.Equal(7L,await Scalar(e,"SELECT COUNT(*) FROM AspNetUsers"));
        }
        await using var second=new DemoStartupWebApplicationFactory(e);
        using var current=second.CreateIdentityClient();await Ready(second,current);
        foreach(var pair in new[]{("AspNetUsers",6L),("Categories",3L),("Trainings",4L),("TrainingMoments",7L),("Registrations",4L),("WaitlistEntries",2L)})
            Assert.Equal(pair.Item2,await Scalar(e,"SELECT COUNT(*) FROM "+pair.Item1));
        Assert.Equal(1L,await Scalar(e,"SELECT COUNT(*) FROM Trainings WHERE Id=300301 AND Title='Excel basis'"));
    }

    // De kalender wordt per start opnieuw berekend; GET tijdens een sessie wijzigt geen planning.
    [Fact]
    public async Task Same_next_and_DST_days_have_no_in_session_nudging()
    {
        await using var e=new DemoStartupTestEnvironment();
        foreach(var instant in new[]{"2026-10-23T22:30:00Z","2026-10-23T22:45:00Z","2026-10-24T22:30:00Z","2026-10-25T23:30:00Z"})
        {
            e.Clock.UtcNow=DateTimeOffset.Parse(instant);
            await using var factory=new DemoStartupWebApplicationFactory(e);
            using var browser=factory.CreateIdentityClient();await Ready(factory,browser);
            var day=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.Clock.UtcNow,TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels")).DateTime);
            await factory.WithServicesAsync(async sp=>{
                var m=await sp.GetRequiredService<ApplicationDbContext>().TrainingMoments.AsNoTracking().SingleAsync(x=>x.Id==300401);
                Assert.Equal(day.AddDays(7),m.Date);Assert.Equal(new TimeOnly(9,0),m.StartTime);
            });
            var before=await Digest(factory);e.Clock.UtcNow=e.Clock.UtcNow.AddDays(2);
            using var page=await browser.GetAsync("/Identity/Account/Login");Assert.Equal(HttpStatusCode.OK,page.StatusCode);
            Assert.Equal(before,await Digest(factory));
        }
    }

    [Fact]
    public async Task Two_non_demo_hosts_preserve_rows_and_have_no_hint_or_offer()
    {
        await using var e=new DemoStartupTestEnvironment();string before;
        await using(var first=new DemoStartupWebApplicationFactory(e,demoEnabled:false))
        {
            using var browser=first.CreateIdentityClient();Assert.False(first.Services.GetRequiredService<DemoCredentials>().IsReady);
            await first.WithServicesAsync(async sp=>{
                await TestAccountData.CreateAsync(sp,RequestedAccountType.Employee);
                await TrainingTestData.CreateCategoryAsync(sp,"Bewaarde niet-demo metadata");
            });before=await Digest(first);
        }
        await using var second=new DemoStartupWebApplicationFactory(e,demoEnabled:false);
        using var current=second.CreateIdentityClient();
        Assert.False(second.Services.GetRequiredService<DemoCredentials>().IsReady);Assert.Equal(before,await Digest(second));
        Assert.Equal(1L,await Scalar(e,"SELECT COUNT(*) FROM AspNetUsers"));
        Assert.Equal(0L,await Scalar(e,"SELECT COUNT(*) FROM TrainingMoments"));
    }

    // Beide mogelijke eerste hosts houden dezelfde lease; de tweede host mag niet schrijven.
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Active_host_refuses_second_host_without_HTTP_or_changes(bool firstDemo)
    {
        await using var e=new DemoStartupTestEnvironment();
        await using var first=new DemoStartupWebApplicationFactory(e,demoEnabled:firstDemo);
        using var browser=first.CreateIdentityClient();var before=await Digest(first);
        await using var second=new DemoStartupWebApplicationFactory(e);
        Assert.ThrowsAny<Exception>(()=>second.CreateIdentityClient());Assert.Equal(before,await Digest(first));
        using var page=await browser.GetAsync("/Identity/Account/Login");Assert.Equal(HttpStatusCode.OK,page.StatusCode);
    }

    // Een late echte SQL-fout verhindert ook de HTTP-host; recovery gebruikt uitsluitend dit eigen doel.
    [Fact]
    public async Task Real_SQL_fault_matrix_aborts_Program_before_pipeline()
    {
        foreach(var point in new[]{"schema","reference","account","offer"})
        {
            await using var e=new DemoStartupTestEnvironment();
            var failure=new DemoStartupFailureInterceptor();failure.Arm(point);
            await using var factory=new DemoStartupWebApplicationFactory(e,interceptor:failure);
            Assert.ThrowsAny<Exception>(()=>factory.CreateIdentityClient());
            Assert.True(failure.Triggered);Assert.True(failure.SawSqlWrite);
            if(point=="offer")
            {
                Assert.Equal(6L,await Scalar(e,"SELECT COUNT(*) FROM AspNetUsers"));
                Assert.Equal(0L,await Scalar(e,"SELECT COUNT(*) FROM Categories"));
                Assert.Equal(0L,await Scalar(e,"SELECT COUNT(*) FROM TrainingMoments"));
            }
            await using var recovery=new DemoStartupWebApplicationFactory(e);
            using var browser=recovery.CreateIdentityClient();await Ready(recovery,browser);
        }
    }

    // Dezelfde account-ID en keyring bewijzen stampinvalidatie, geen toevallig onleesbare cookie.
    [Fact]
    public async Task Old_real_cookie_is_rejected_and_new_login_works_with_shared_keyring()
    {
        await using var e=new DemoStartupTestEnvironment();string cookie;string stamp;
        var account=DemoAccountCatalog.All.Single(x=>x.Id==DemoAccountCatalog.InternalInstructorId);
        await using(var first=new DemoStartupWebApplicationFactory(e))
        {
            using var browser=first.CreateIdentityClient();
            Assert.True(first.Services.GetRequiredService<DemoCredentials>().TryGetReadyHint(out var hint));
            using var login=await browser.LoginAsync(new TestIdentity(account.Id,account.Email,hint!.Password,null));
            Assert.Equal(HttpStatusCode.Redirect,login.StatusCode);
            cookie=string.Join("; ",login.Headers.GetValues("Set-Cookie").Select(x=>x.Split(';')[0]));
            using var allowed=await browser.GetAsync("/TrainingManagement");Assert.Equal(HttpStatusCode.OK,allowed.StatusCode);
            stamp=await Stamp(first,account.Id);
        }
        await using var second=new DemoStartupWebApplicationFactory(e);
        using var current=second.CreateIdentityClient();Assert.True(stamp!=await Stamp(second,account.Id));
        using(var stale=second.CreateIdentityClient())
        {
            stale.RawClient.DefaultRequestHeaders.Add("Cookie",cookie);
            using var refused=await stale.GetAsync("/TrainingManagement");Assert.Equal(HttpStatusCode.Redirect,refused.StatusCode);
            Assert.True(refused.Headers.Location?.OriginalString.Contains("/Identity/Account/Login",StringComparison.Ordinal)==true);
        }
        // Alleen booleans/statussen verlaten de assertions: geen stamp, cookie of wachtwoord in output.
        Assert.True(second.Services.GetRequiredService<DemoCredentials>().TryGetReadyHint(out var fresh));
        using var newLogin=await current.LoginAsync(new TestIdentity(account.Id,account.Email,fresh!.Password,null));
        Assert.Equal(HttpStatusCode.Redirect,newLogin.StatusCode);
        using var permitted=await current.GetAsync("/TrainingManagement");Assert.Equal(HttpStatusCode.OK,permitted.StatusCode);
    }

    [Fact]
    public async Task Held_WAL_reader_refuses_reset_and_exact_target_preserves_neighbors()
    {
        await using var e=new DemoStartupTestEnvironment();var directory=Path.GetDirectoryName(e.DatabasePath)!;
        var neighbor=Path.Combine(directory,"neighbor.db");var marker=Path.Combine(directory,"bewaard.txt");
        await using(var connection=new SqliteConnection($"Data Source={neighbor};Pooling=False"))
        {await connection.OpenAsync();await using var cmd=connection.CreateCommand();cmd.CommandText="CREATE TABLE Neighbor(Value INTEGER); INSERT INTO Neighbor VALUES(91)";await cmd.ExecuteNonQueryAsync();}
        File.WriteAllText(marker,"Fictief bewijs buiten het exacte doel");var neighborHash=Hash(neighbor);var markerHash=Hash(marker);
        await using(var connection=new SqliteConnection(e.ConnectionString))
        {await connection.OpenAsync();await using var cmd=connection.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; CREATE TABLE Held(Value INTEGER); INSERT INTO Held VALUES(7)";await cmd.ExecuteNonQueryAsync();}
        var before=Hash(e.DatabasePath);
        await using(var connection=new SqliteConnection(e.ConnectionString))
        {
            await connection.OpenAsync();await using var tx=connection.BeginTransaction(deferred:true);
            await using var cmd=connection.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Value FROM Held";
            await using var reader=await cmd.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());
            await using var blocked=new DemoStartupWebApplicationFactory(e);
            Assert.ThrowsAny<Exception>(()=>blocked.CreateIdentityClient());
            Assert.Equal(7L,reader.GetInt64(0));Assert.True(File.Exists(e.DatabasePath));
        }
        Assert.Equal(before,Hash(e.DatabasePath));
        await using var recovery=new DemoStartupWebApplicationFactory(e);
        using var browser=recovery.CreateIdentityClient();await Ready(recovery,browser);
        Assert.Equal(0L,await Scalar(e,"SELECT COUNT(*) FROM sqlite_master WHERE name='Held'"));
        Assert.Equal(neighborHash,Hash(neighbor));Assert.Equal(markerHash,Hash(marker));
    }

    private static async Task Ready(DemoStartupWebApplicationFactory factory,IdentityHttpClient browser)
    {Assert.True(factory.Services.GetRequiredService<DemoCredentials>().IsReady);using var r=await browser.GetAsync("/Identity/Account/Login");Assert.Equal(HttpStatusCode.OK,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore==true);}
    private static async Task<string> Digest(DemoStartupWebApplicationFactory factory)
    {string value="";await factory.WithServicesAsync(async sp=>value=await TrainingTestData.DigestAsync(sp));return value;}
    private static async Task<string> Stamp(DemoStartupWebApplicationFactory factory,string id)
    {string value="";await factory.WithServicesAsync(async sp=>value=(await sp.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(x=>x.Id==id)).SecurityStamp!);return value;}
    private static async Task<long> Scalar(DemoStartupTestEnvironment e,string sql)
    {await using var connection=new SqliteConnection(e.ConnectionString);await connection.OpenAsync();await using var cmd=connection.CreateCommand();cmd.CommandText=sql;return Convert.ToInt64(await cmd.ExecuteScalarAsync());}
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

