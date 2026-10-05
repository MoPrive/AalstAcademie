using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Enrolment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace AalstAcademie.Tests.Web;

/// <summary>De echte Program draait op Kestrel en eigen filedatabases. Guards falen vóór een listener wordt vrijgegeven.</summary>
[Collection("Revision02 provider races")]
public class EnrolmentStartupTests
{
    [Theory]
    [InlineData("TwoDemoStartsRecreateSeedAndNewStamps")]
    [InlineData("TwoNonDemoStartsPreserveExistingOwnRows")]
    [InlineData("ProductionDemoRefusedBeforeListener")]
    [InlineData("MismatchedResetTargetRefused")]
    [InlineData("InUseDatabaseRefused")]
    [InlineData("SameDbSecondHostLeaseRefused")]
    [InlineData("SeedFailureBeforeHttpAndHint")]
    [InlineData("LegacyNonemptyNonDemoRefusedBeforeSchema")]
    public async Task Own_clean_copy_startup(string variant)
    {
        await using var e = new DemoStartupTestEnvironment();
        if (variant == "TwoDemoStartsRecreateSeedAndNewStamps")
        {
            string originalStamp = ""; Guid originalVersion = default;
            await using (var first = Factory(e, 53761))
            {
                using var client = Start(first); await Ready(first, client);
                await first.WithServicesAsync(async sp => {
                    var db = sp.GetRequiredService<ApplicationDbContext>();
                    originalStamp = (await db.Users.SingleAsync(x => x.Id == "demo-002-employee")).SecurityStamp!;
                    var moment = await db.TrainingMoments.SingleAsync(x => x.Id == 300406); originalVersion = moment.Version;
                    moment.MaximumParticipants = 2; await db.SaveChangesAsync(); });
            }
            await using var second = Factory(e, 53761); using var fresh = Start(second); await Ready(second, fresh);
            await second.WithServicesAsync(async sp => {
                var db = sp.GetRequiredService<ApplicationDbContext>();
                Assert.True(originalStamp != (await db.Users.AsNoTracking().SingleAsync(x => x.Id == "demo-002-employee")).SecurityStamp);
                var moment = await db.TrainingMoments.AsNoTracking().SingleAsync(x => x.Id == 300406);
                Assert.NotEqual(originalVersion, moment.Version); Assert.Equal(1, moment.MaximumParticipants);
                Assert.Equal(6, await db.Users.CountAsync()); Assert.Equal(7, await db.TrainingMoments.CountAsync());
                Assert.Equal(4, await db.Registrations.CountAsync()); Assert.Equal(2, await db.WaitlistEntries.CountAsync()); });
            return;
        }
        if (variant == "TwoNonDemoStartsPreserveExistingOwnRows")
        {
            string before = "";
            await using (var first = Factory(e, 53762, demo: false))
            {
                using var client = Start(first); Assert.False(first.Services.GetRequiredService<DemoCredentials>().IsReady);
                await first.WithServicesAsync(async sp => {
                    var owner = await TrainingTestData.CreateOwnerAsync(sp); var training = await TrainingTestData.CreateTrainingAsync(sp, owner, scope: TrainingAudienceScope.AllDepartments);
                    var moment = await TrainingTestData.CreateMomentAsync(sp, training, maximumParticipants: 1);
                    await TrainingTestData.CreateRegistrationAsync(sp, moment, applicant: owner);
                    var employee = await TestAccountData.CreateAsync(sp, RequestedAccountType.Employee, AccountApprovalStatus.Approved, roles: [RoleNames.Medewerker]);
                    await TrainingTestData.CreateWaitlistEntryAsync(sp, training, applicant: employee);
                    before = await TrainingTestData.DigestAsync(sp); });
            }
            await using var second = Factory(e, 53762, demo: false); using var fresh = Start(second);
            Assert.False(second.Services.GetRequiredService<DemoCredentials>().IsReady);
            await second.WithServicesAsync(async sp => { Assert.Equal(before, await TrainingTestData.DigestAsync(sp)); var db = sp.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(1, await db.Registrations.CountAsync()); Assert.Equal(1, await db.WaitlistEntries.CountAsync()); Assert.False(await db.Users.AnyAsync(x => x.DemoSeedKey != null)); });
            return;
        }
        if (variant == "ProductionDemoRefusedBeforeListener")
        {
            e.EnvironmentName = Environments.Production;
            await using var factory = Factory(e, 53763);
            Assert.ThrowsAny<Exception>(() => Start(factory)); Assert.False(File.Exists(e.DatabasePath)); AssertFree(53763); return;
        }
        if (variant == "MismatchedResetTargetRefused")
        {
            var neighbor = Path.Combine(Path.GetDirectoryName(e.DatabasePath)!, "wrong-reset.db"); File.WriteAllText(neighbor, "Fictieve buurmarker");
            var before = Hash(neighbor);
            await using var factory = Factory(e, 53763, resetTargetPath: neighbor);
            Assert.ThrowsAny<Exception>(() => Start(factory)); Assert.Equal(before, Hash(neighbor)); Assert.False(File.Exists(e.DatabasePath)); AssertFree(53763); return;
        }
        if (variant == "SameDbSecondHostLeaseRefused")
        {
            await using var first = Factory(e, 53761); using var client = Start(first); await Ready(first, client);
            var before = await Digest(first);
            await using var second = Factory(e, 53764);
            Assert.ThrowsAny<Exception>(() => Start(second)); AssertFree(53764); Assert.Equal(before, await Digest(first));
            using var stillReady = await client.GetAsync("/Identity/Account/Login"); Assert.Equal(HttpStatusCode.OK, stillReady.StatusCode); return;
        }
        if (variant == "SeedFailureBeforeHttpAndHint")
        {
            var failure = new DemoStartupFailureInterceptor(); failure.Arm("join");
            await using var factory = Factory(e, 53763, failure: failure);
            Assert.ThrowsAny<Exception>(() => Start(factory)); Assert.True(failure.Triggered && failure.SawSqlWrite); AssertFree(53763);
            Assert.Equal(2L, await Scalar(e, "SELECT COUNT(*) FROM Registrations")); Assert.Equal(0L, await Scalar(e, "SELECT COUNT(*) FROM WaitlistEntries"));
            // De bijbehorende bootstrap-unitproef controleert ook de ongepubliceerde singletonhint.
            return;
        }
        if (variant == "LegacyNonemptyNonDemoRefusedBeforeSchema")
        {
            await using (var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(e.ConnectionString).Options))
            {
                await db.GetService<IMigrator>().MigrateAsync(LegacyTrainingMigrationPreflight.RevisionMigration);
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO AspNetUsers(Id,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount,AccountApprovalStatus,IsBlocked,ManagerName)
                    VALUES('legacy',0,0,0,0,0,0,0,'Bewaarde naam');
                    """);
            }
            var before = Hash(e.DatabasePath); await using var factory = Factory(e, 53763, demo: false);
            Assert.ThrowsAny<Exception>(() => Start(factory)); AssertFree(53763); Assert.Equal(before, Hash(e.DatabasePath));
            Assert.Equal(1L, await Scalar(e, "SELECT COUNT(*) FROM AspNetUsers WHERE ManagerName='Bewaarde naam'")); return;
        }
        Assert.Equal("InUseDatabaseRefused", variant);
        await using (var seed = new SqliteConnection(e.ConnectionString))
        { await seed.OpenAsync(); await using var cmd = seed.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE Held(Value INTEGER); INSERT INTO Held VALUES(71)"; await cmd.ExecuteNonQueryAsync(); }
        var original = Hash(e.DatabasePath);
        await using (var connection = new SqliteConnection(e.ConnectionString))
        {
            await connection.OpenAsync(); await using var tx = connection.BeginTransaction(deferred: true);
            await using var cmd = connection.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT Value FROM Held";
            await using var reader = await cmd.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            await using var factory = Factory(e, 53763); Assert.ThrowsAny<Exception>(() => Start(factory)); AssertFree(53763); Assert.Equal(71L, reader.GetInt64(0));
        }
        Assert.Equal(original, Hash(e.DatabasePath));
    }
    private static DemoStartupWebApplicationFactory Factory(DemoStartupTestEnvironment e, int port, bool demo = true, DemoStartupFailureInterceptor? failure = null, string? resetTargetPath = null)
    {
        AssertFree(port); var factory = new DemoStartupWebApplicationFactory(e, demoEnabled: demo, interceptor: failure, resetTargetPath: resetTargetPath);
        factory.UseKestrel(port); factory.ClientOptions.BaseAddress = new Uri($"http://127.0.0.1:{port}");
        factory.ClientOptions.AllowAutoRedirect = false; return factory;
    }
    private static void AssertFree(int port) => Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), x => x.Port == port);
    private static HttpClient Start(WebApplicationFactory<Program> factory) => factory.CreateClient(factory.ClientOptions);
    private static async Task Ready(DemoStartupWebApplicationFactory factory, HttpClient client)
    { Assert.True(factory.Services.GetRequiredService<DemoCredentials>().IsReady); using var page = await client.GetAsync("/Identity/Account/Login"); Assert.Equal(HttpStatusCode.OK, page.StatusCode); }
    private static async Task<string> Digest(DemoStartupWebApplicationFactory factory)
    { string value = ""; await factory.WithServicesAsync(async sp => value = await TrainingTestData.DigestAsync(sp)); return value; }
    private static async Task<long> Scalar(DemoStartupTestEnvironment e, string sql)
    { await using var connection = new SqliteConnection(e.ConnectionString); await connection.OpenAsync(); await using var cmd = connection.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
