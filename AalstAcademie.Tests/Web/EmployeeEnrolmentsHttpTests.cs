using System.Net;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Web;

/// <summary>Echte cookies/antiforgery plus onafhankelijke opslagreadback voor ieder toegestaan of afgewezen POST-pad.</summary>
public class EmployeeEnrolmentsHttpTests
{
    [Theory]
    [InlineData("RequestSuccessPrgRequestedOnly")]
    [InlineData("JoinSuccessPrgBufferOnly")]
    [InlineData("RequestCsrfMissing")]
    [InlineData("JoinCsrfMissing")]
    [InlineData("RequestPostedActorRejected")]
    [InlineData("JoinPostedStatusRejected")]
    [InlineData("RequestPostedPriceAndDepartmentRejected")]
    [InlineData("JoinPostedTimestampOwnerRejected")]
    [InlineData("RequiredMotivation400PreservesInput")]
    [InlineData("TooLongMotivation400")]
    [InlineData("NulMotivation400")]
    [InlineData("NoConsent400NoWrite")]
    [InlineData("FullRequest409NoAutoBuffer")]
    [InlineData("StaleTraining409PreservesOriginalTokens")]
    [InlineData("StaleMoment409PreservesOriginalTokens")]
    [InlineData("StartPassed409PreservesInput")]
    [InlineData("DuplicateRequest409")]
    [InlineData("DuplicateJoin409PreservesOldData")]
    [InlineData("DirectPlaceFreesJoin409Reread")]
    [InlineData("WrongTarget403NoWrite")]
    [InlineData("RoleLostAfterGet403")]
    [InlineData("StorageFailure503NoLeak")]
    [InlineData("RequestCsrfInvalid")]
    [InlineData("JoinCsrfInvalid")]
    public async Task Post_boundary(string variant)
    {
        var join = variant.StartsWith("Join", StringComparison.Ordinal) || variant is "NoConsent400NoWrite" or "DuplicateJoin409PreservesOldData" or "DirectPlaceFreesJoin409Reread";
        await using var f = await EnrolmentHttpFixture.CreateAsync(maximum: 1);
        Registration? occupied = null;
        if (join || variant == "FullRequest409NoAutoBuffer") occupied = await f.RegistrationAsync(user: f.Owner);
        if (variant == "DuplicateRequest409") await f.RegistrationAsync();
        if (variant == "DuplicateJoin409PreservesOldData") await f.BufferAsync();
        if (variant == "RequiredMotivation400PreservesInput")
            await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).RequiresMotivation = true);
        DateTimeOffset startUtc = default;
        if (variant == "StartPassed409PreservesInput")
        {
            // Een oude oktober-cookie zou ook verlopen bij de sprong naar december. Lees het formulier
            // met een verse sessie één minuut vóór de opgeslagen start; alleen de momentgrens verstrijkt.
            await f.Factory.WithServicesAsync(services =>
            {
                var schedule = services.GetRequiredService<TrainingSchedule>().Validate(f.Moment.Date, f.Moment.StartTime, f.Moment.EndTime);
                Assert.True(schedule.Succeeded); startUtc = schedule.Value!.StartUtc;
                ((FixedTimeProvider)services.GetRequiredService<TimeProvider>()).UtcNow = startUtc.AddMinutes(-1);
                return Task.CompletedTask;
            });
            await f.LoginAsync(f.Employee);
            using var open = await f.Browser.GetAsync(f.MomentUrl);
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
            Assert.Equal(f.Moment.Version.ToString(), EnrolmentHttpFixture.Value(await open.Content.ReadAsStringAsync(), "ExpectedMomentVersion"));
        }
        // Cookie en anti-CSRF-token worden vóór de wijziging gelezen, zodat de test echt een oude browser heeft.
        var token = await f.Browser.GetAntiforgeryTokenAsync("/MyRegistrations");
        var form = f.Form(join);
        var expected = HttpStatusCode.BadRequest;
        if (variant.Contains("Success", StringComparison.Ordinal)) expected = HttpStatusCode.Redirect;
        if (variant.Contains("409", StringComparison.Ordinal)) expected = HttpStatusCode.Conflict;
        if (variant == "RequestPostedActorRejected") form["ApplicationUserId"] = f.Owner.UserId;
        if (variant == "JoinPostedStatusRejected") form["Status"] = "Confirmed";
        if (variant == "RequestPostedPriceAndDepartmentRejected") { form["ExternalTotalPriceEuros"] = "0"; form["DepartmentId"] = "123"; form["momentId"] = int.MaxValue.ToString(); }
        if (variant == "JoinPostedTimestampOwnerRejected") { form["JoinedAtUtc"] = "2000-01-01"; form["InstructorUserId"] = f.Owner.UserId; }
        if (variant == "RequiredMotivation400PreservesInput") form["Motivation"] = "   ";
        if (variant == "TooLongMotivation400") form["Motivation"] = new string('x', 4001);
        if (variant == "NulMotivation400") form["Motivation"] = "voor\0na";
        if (variant == "NoConsent400NoWrite") form.Remove("Consent");
        if (variant == "StaleTraining409PreservesOriginalTokens")
            await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).Version = Guid.NewGuid());
        if (variant == "StaleMoment409PreservesOriginalTokens")
            await f.MutateAsync(async db => (await db.TrainingMoments.SingleAsync()).Version = Guid.NewGuid());
        if (variant == "StartPassed409PreservesInput")
        {
            await f.Factory.WithServicesAsync(services =>
            { ((FixedTimeProvider)services.GetRequiredService<TimeProvider>()).UtcNow = startUtc; return Task.CompletedTask; });
        }
        if (variant == "DirectPlaceFreesJoin409Reread")
            await f.MutateAsync(async db => (await db.Registrations.SingleAsync(x => x.Id == occupied!.Id)).Status = RegistrationStatus.Cancelled);
        if (variant == "WrongTarget403NoWrite")
        { await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).AudienceScope = TrainingAudienceScope.Unassigned); expected = HttpStatusCode.Forbidden; }
        if (variant == "RoleLostAfterGet403") { await f.RemoveRoleAsync(); expected = HttpStatusCode.Forbidden; }
        if (variant == "StorageFailure503NoLeak") { f.Fault.Arm("request"); expected = HttpStatusCode.ServiceUnavailable; }
        if (!variant.Contains("CsrfMissing", StringComparison.Ordinal))
            form["__RequestVerificationToken"] = variant.Contains("CsrfInvalid", StringComparison.Ordinal) ? "invalid-token" : token;
        var before = await f.DigestAsync();
        using var response = await f.Browser.RawClient.PostAsync($"/EmployeeEnrolments/{(join ? "JoinWaitlist" : "Request")}/{f.Moment.Id}", new FormUrlEncodedContent(form));
        Assert.Equal(expected, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        if (expected == HttpStatusCode.Redirect)
        {
            Assert.Equal("/MyRegistrations", response.Headers.Location!.OriginalString);
            await f.Factory.WithServicesAsync(async services =>
            {
                var db = services.GetRequiredService<ApplicationDbContext>();
                if (join)
                {
                    var row = await db.WaitlistEntries.AsNoTracking().SingleAsync();
                    Assert.Equal(f.Employee.UserId, row.ApplicationUserId); Assert.Equal(f.Training.Id, row.TrainingId);
                    Assert.Null(row.ClosedAtUtc); Assert.NotEqual(Guid.Empty, row.Version); Assert.Equal(form["Motivation"], row.Motivation);
                    Assert.Equal(1, await db.Registrations.CountAsync());
                    Assert.Equal(services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, row.JoinedAtUtc);
                }
                else
                {
                    var row = await db.Registrations.AsNoTracking().SingleAsync();
                    Assert.Equal(f.Employee.UserId, row.ApplicationUserId); Assert.Equal(f.Moment.Id, row.TrainingMomentId);
                    Assert.Equal(RegistrationStatus.Requested, row.Status); Assert.NotEqual(Guid.Empty, row.Version);
                    Assert.Equal(form["Motivation"], row.Motivation); Assert.Empty(await db.WaitlistEntries.ToListAsync());
                    Assert.Equal(services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, row.RequestedAtUtc);
                }
            });
        }
        else
        {
            Assert.Equal(before, await f.DigestAsync());
            if (expected is HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable || variant is "RequiredMotivation400PreservesInput" or "TooLongMotivation400")
            {
                Assert.Equal(form["ExpectedTrainingVersion"], EnrolmentHttpFixture.Value(html, "ExpectedTrainingVersion"));
                Assert.Equal(form["ExpectedMomentVersion"], EnrolmentHttpFixture.Value(html, "ExpectedMomentVersion"));
                Assert.Contains(WebUtility.HtmlEncode(form["Motivation"]), html, StringComparison.Ordinal);
            }
            if (expected == HttpStatusCode.Conflict) Assert.Contains("opnieuw", html, StringComparison.Ordinal);
            if (variant == "StorageFailure503NoLeak")
            { Assert.True(f.Fault.Triggered); Assert.DoesNotContain("Geïsoleerde testfout", html, StringComparison.Ordinal); Assert.DoesNotContain("SQLite", html, StringComparison.OrdinalIgnoreCase); }
        }
    }
}
