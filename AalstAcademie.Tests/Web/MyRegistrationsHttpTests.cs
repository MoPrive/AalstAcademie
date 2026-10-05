using System.Net;
using System.Text.RegularExpressions;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Web;

/// <summary>Eigen historie, onafhankelijk gepagineerde lijsten en concrete intrekking zonder verlies van oude rijen.</summary>
public class MyRegistrationsHttpTests
{
    [Theory]
    [InlineData("OwnRowsOnly")]
    [InlineData("SeparatePage20")]
    [InlineData("FourStatusHistoryAndReasonEncoded")]
    [InlineData("ActiveClosedBufferHistory")]
    [InlineData("GetWithdrawNoWrite")]
    [InlineData("SuccessfulConcreteWithdrawPrg")]
    [InlineData("OtherUserWithdraw404")]
    [InlineData("UnknownWithdraw404")]
    [InlineData("StaleWithdraw409KeepsVersion")]
    [InlineData("ClosedWithdraw409")]
    [InlineData("OldPostCannotCloseRejoinedRow")]
    [InlineData("RoleTargetLossStillHistoryAndWithdraw")]
    [InlineData("BlockedStampBoundaryDenied")]
    [InlineData("MissingCsrf400")]
    [InlineData("PostedUserTrainingClosedAtRejected")]
    [InlineData("StorageFailure503PreservesVersion")]
    public async Task Own_history(string variant)
    {
        await using var f = await EnrolmentHttpFixture.CreateAsync();
        var entry = await f.BufferAsync(variant == "OtherUserWithdraw404" ? f.Owner : null, variant == "ClosedWithdraw409");
        var path = "/MyRegistrations/Withdraw/" + (variant == "UnknownWithdraw404" ? int.MaxValue : entry.Id);
        if (variant is "OwnRowsOnly" or "SeparatePage20" or "FourStatusHistoryAndReasonEncoded" or "ActiveClosedBufferHistory")
        {
            var own = await f.RegistrationAsync();
            var other = await f.RegistrationAsync(user: f.Owner); var foreign = await f.BufferAsync(f.Owner);
            if (variant == "SeparatePage20")
                for (var i = 0; i < 20; i++) { await f.BufferAsync(closed: true); await f.RegistrationAsync(RegistrationStatus.Cancelled); }
            if (variant == "ActiveClosedBufferHistory") await f.BufferAsync(closed: true);
            if (variant == "FourStatusHistoryAndReasonEncoded")
            {
                await f.Factory.WithServicesAsync(async services =>
                {
                    var db = services.GetRequiredService<ApplicationDbContext>();
                    var actor = await db.Users.SingleAsync(x => x.Id == f.Employee.UserId);
                    var confirmedMoment = await TrainingTestData.CreateMomentAsync(services, f.Training, location: f.Room, date: f.Moment.Date.AddDays(1));
                    await TrainingTestData.CreateRegistrationAsync(services, confirmedMoment, RegistrationStatus.Confirmed, actor);
                    var refused = await TrainingTestData.CreateRegistrationAsync(services, f.Moment, RegistrationStatus.Refused, actor);
                    refused.DecisionReason = "<script>reden</script>"; await db.SaveChangesAsync();
                    await TrainingTestData.CreateRegistrationAsync(services, f.Moment, RegistrationStatus.Cancelled, actor);
                });
            }
            var beforeRead = await f.DigestAsync(); using var get = await f.Browser.GetAsync("/MyRegistrations");
            Assert.Equal(HttpStatusCode.OK, get.StatusCode); var html = await get.Content.ReadAsStringAsync();
            Assert.DoesNotContain($"data-registration-id=\"{other.Id}\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain($"data-waitlist-id=\"{foreign.Id}\"", html, StringComparison.Ordinal);
            if (variant == "OwnRowsOnly") { Assert.Contains($"data-registration-id=\"{own.Id}\"", html, StringComparison.Ordinal); Assert.Contains($"data-waitlist-id=\"{entry.Id}\"", html, StringComparison.Ordinal); }
            if (variant == "SeparatePage20")
            {
                Assert.Equal(20, Regex.Matches(html, "data-registration-id=").Count); Assert.Equal(20, Regex.Matches(html, "data-waitlist-id=").Count);
                using var next = await f.Browser.GetAsync("/MyRegistrations?registrationPage=2&waitlistPage=2");
                var second = await next.Content.ReadAsStringAsync(); Assert.Equal(HttpStatusCode.OK, next.StatusCode);
                Assert.Single(Regex.Matches(second, "data-registration-id=").Cast<Match>()); Assert.Single(Regex.Matches(second, "data-waitlist-id=").Cast<Match>());
            }
            if (variant == "FourStatusHistoryAndReasonEncoded")
            { foreach (var status in new[] { "Aangevraagd", "Bevestigd", "Geweigerd", "Geannuleerd" }) Assert.Contains(status, html, StringComparison.Ordinal); Assert.Contains("&lt;script&gt;reden", html, StringComparison.Ordinal); Assert.DoesNotContain("<script>reden", html, StringComparison.Ordinal); }
            if (variant == "ActiveClosedBufferHistory") { Assert.Contains("Actieve wachtlijstaansluiting", html, StringComparison.Ordinal); Assert.Contains("Gesloten (UTC)", html, StringComparison.Ordinal); }
            Assert.Equal(beforeRead, await f.DigestAsync()); return;
        }
        if (variant == "BlockedStampBoundaryDenied")
        {
            using var second = f.Factory.CreateIdentityClient(); using var login = await second.LoginAsync(f.Employee); Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.UserId)).IsBlocked = true);
            var blockedBefore = await f.DigestAsync(); using var blocked = await f.Browser.GetAsync("/MyRegistrations"); Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode); Assert.Equal(blockedBefore, await f.DigestAsync());
            await f.MutateAsync(async db => { var u = await db.Users.SingleAsync(x => x.Id == f.Employee.UserId); u.IsBlocked = false; u.SecurityStamp = Guid.NewGuid().ToString(); });
            var stampBefore = await f.DigestAsync(); using var stamp = await second.GetAsync(path); Assert.Equal(HttpStatusCode.Redirect, stamp.StatusCode); Assert.Equal(stampBefore, await f.DigestAsync()); return;
        }
        if (variant == "GetWithdrawNoWrite")
        { var beforeGet = await f.DigestAsync(); using var get = await f.Browser.GetAsync(path); Assert.Equal(HttpStatusCode.OK, get.StatusCode); Assert.Equal(beforeGet, await f.DigestAsync()); return; }
        var form = new Dictionary<string, string> { ["ExpectedVersion"] = entry.Version.ToString() };
        if (variant == "StaleWithdraw409KeepsVersion") await f.MutateAsync(async db => (await db.WaitlistEntries.SingleAsync()).Version = Guid.NewGuid());
        WaitlistEntry? rejoined = null;
        if (variant == "OldPostCannotCloseRejoinedRow")
        { await f.MutateAsync(async db => { var old = await db.WaitlistEntries.SingleAsync(); old.ClosedAtUtc = DateTime.UtcNow; old.Version = Guid.NewGuid(); }); rejoined = await f.BufferAsync(); }
        if (variant == "RoleTargetLossStillHistoryAndWithdraw")
        {
            await f.RemoveRoleAsync(); await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).AudienceScope = TrainingAudienceScope.Unassigned);
            using var history = await f.Browser.GetAsync("/MyRegistrations"); Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            using var read = await f.Browser.GetAsync(path); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
        if (variant == "PostedUserTrainingClosedAtRejected") { form["ApplicationUserId"] = f.Owner.UserId; form["TrainingId"] = "0"; form["ClosedAtUtc"] = "2000-01-01"; form["Id"] = int.MaxValue.ToString(); }
        if (variant == "StorageFailure503PreservesVersion") f.Fault.Arm("withdraw");
        var before = await f.DigestAsync();
        using var response = variant == "MissingCsrf400" ? await f.Browser.RawClient.PostAsync(path, new FormUrlEncodedContent(form)) : await f.PostAsync(path, form);
        var expected = variant switch
        {
            "SuccessfulConcreteWithdrawPrg" or "RoleTargetLossStillHistoryAndWithdraw" => HttpStatusCode.Redirect,
            "OtherUserWithdraw404" or "UnknownWithdraw404" => HttpStatusCode.NotFound,
            "StaleWithdraw409KeepsVersion" or "ClosedWithdraw409" or "OldPostCannotCloseRejoinedRow" => HttpStatusCode.Conflict,
            "StorageFailure503PreservesVersion" => HttpStatusCode.ServiceUnavailable,
            _ => HttpStatusCode.BadRequest
        };
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Redirect)
        {
            Assert.Equal("/MyRegistrations", response.Headers.Location!.OriginalString);
            await f.Factory.WithServicesAsync(async services =>
            { var row = await services.GetRequiredService<ApplicationDbContext>().WaitlistEntries.AsNoTracking().SingleAsync(); Assert.NotNull(row.ClosedAtUtc); Assert.NotEqual(entry.Version, row.Version); Assert.Equal(entry.JoinedAtUtc, row.JoinedAtUtc); Assert.Equal(entry.Motivation, row.Motivation); Assert.Equal(services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, row.ClosedAtUtc); });
        }
        else
        {
            Assert.Equal(before, await f.DigestAsync());
            if (expected is HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable)
                Assert.Equal(form["ExpectedVersion"], EnrolmentHttpFixture.Value(await response.Content.ReadAsStringAsync(), "ExpectedVersion"));
            if (rejoined is not null) await f.Factory.WithServicesAsync(async services => Assert.Null((await services.GetRequiredService<ApplicationDbContext>().WaitlistEntries.SingleAsync(x => x.Id == rejoined.Id)).ClosedAtUtc));
            if (variant == "StorageFailure503PreservesVersion") Assert.True(f.Fault.Triggered);
        }
    }
}
