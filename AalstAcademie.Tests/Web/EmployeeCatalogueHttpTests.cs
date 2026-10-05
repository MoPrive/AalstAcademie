using System.Net;
using System.Text.RegularExpressions;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Tests.Web;

/// <summary>De catalogus moet via echte Identity en middleware bereikbaar worden voor een huidige medewerker.</summary>
public class EmployeeCatalogueHttpTests
{
    [Theory]
    [InlineData("AnonymousLoginChallenge")]
    [InlineData("ApprovedEmployeeIndex")]
    [InlineData("WrongDepartmentDetail403")]
    [InlineData("UnknownMoment404")]
    [InlineData("UnassignedDetail403")]
    [InlineData("InstructorWithoutMedewerker403")]
    [InlineData("AdminWithoutMedewerker403")]
    [InlineData("InternalMedewerkerOwnTrainingAllowed")]
    [InlineData("RoleLostBetweenGetsDenied")]
    [InlineData("BlockedCookieDenied")]
    [InlineData("EncodedDescriptionOwnerRoomPrice")]
    [InlineData("NoParticipantContactLeak")]
    [InlineData("Page20CategoryOrder")]
    [InlineData("GetDoesNotWrite")]
    [InlineData("NoEligibleFutureNoBufferForm")]
    [InlineData("NoSprint005EndpointsOrButtons")]
    public async Task Catalogue(string variant)
    {
        await using var f = await EnrolmentHttpFixture.CreateAsync(external: variant == "EncodedDescriptionOwnerRoomPrice");
        var path = "/EmployeeCatalogue";
        var expected = HttpStatusCode.OK;
        if (variant == "AnonymousLoginChallenge")
        {
            using var anonymous = f.Factory.CreateIdentityClient();
            var beforeAnonymous = await f.DigestAsync();
            using var denied = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("/Identity/Account/Login", denied.Headers.Location!.OriginalString, StringComparison.Ordinal);
            Assert.Equal(beforeAnonymous, await f.DigestAsync()); return;
        }
        if (variant == "WrongDepartmentDetail403")
        {
            await f.MutateAsync(async db =>
            {
                var t = await db.Trainings.SingleAsync(x => x.Id == f.Training.Id); t.AudienceScope = TrainingAudienceScope.SelectedDepartments;
                var other = await db.Departments.Where(x => x.Id != f.Employee.DepartmentId).Select(x => x.Id).FirstAsync();
                db.TrainingDepartments.Add(new() { TrainingId = t.Id, DepartmentId = other });
            }); path = f.MomentUrl; expected = HttpStatusCode.Forbidden;
        }
        if (variant == "UnassignedDetail403")
        { await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).AudienceScope = TrainingAudienceScope.Unassigned); path = f.MomentUrl; expected = HttpStatusCode.Forbidden; }
        if (variant == "UnknownMoment404") { path = "/EmployeeCatalogue/Moment/2147483647"; expected = HttpStatusCode.NotFound; }
        if (variant == "InstructorWithoutMedewerker403") { await f.RemoveRoleAsync(f.Owner.UserId); await f.LoginAsync(f.Owner); expected = HttpStatusCode.Forbidden; }
        if (variant == "AdminWithoutMedewerker403") { await f.LoginAsync(f.Admin); expected = HttpStatusCode.Forbidden; }
        if (variant == "InternalMedewerkerOwnTrainingAllowed") { await f.LoginAsync(f.Owner); path = f.MomentUrl; }
        if (variant == "RoleLostBetweenGetsDenied")
        { using var first = await f.Browser.GetAsync(path); Assert.Equal(HttpStatusCode.OK, first.StatusCode); await f.RemoveRoleAsync(); expected = HttpStatusCode.Forbidden; }
        if (variant == "BlockedCookieDenied")
        { await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.UserId)).IsBlocked = true); expected = HttpStatusCode.Redirect; }
        if (variant == "EncodedDescriptionOwnerRoomPrice")
        {
            await f.MutateAsync(async db =>
            {
                (await db.Trainings.SingleAsync()).Description = "<script>alert('tekst')</script>";
                (await db.Locations.SingleAsync()).Name = "<b>zaal</b>";
                (await db.ExternalInstructors.SingleAsync()).OrganizationName = "<i>academie</i>";
            }); path = f.MomentUrl;
        }
        if (variant == "NoParticipantContactLeak") { await f.RegistrationAsync(user: f.Owner); path = f.MomentUrl; }
        if (variant == "Page20CategoryOrder")
        {
            await f.Factory.WithServicesAsync(async services =>
            { for (var i = 1; i <= 20; i++) await TrainingTestData.CreateMomentAsync(services, f.Training, location: f.Room, date: f.Moment.Date.AddDays(i)); });
            path += "?categoryId=" + f.Training.CategoryId;
        }
        if (variant == "NoEligibleFutureNoBufferForm")
            await f.MutateAsync(async db => (await db.TrainingMoments.SingleAsync()).Status = TrainingMomentStatus.Cancelled);
        var before = await f.DigestAsync();
        using var response = await f.Browser.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        if (expected == HttpStatusCode.OK && variant != "NoEligibleFutureNoBufferForm") Assert.Contains(f.Training.Title, html, StringComparison.Ordinal);
        if (variant == "EncodedDescriptionOwnerRoomPrice")
        { Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal); Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal); Assert.Contains("&lt;b&gt;zaal", html, StringComparison.Ordinal); Assert.Contains("&lt;i&gt;academie", html, StringComparison.Ordinal); Assert.Matches("12[,.]30 EUR", html); }
        if (variant == "NoParticipantContactLeak") Assert.DoesNotContain(f.Owner.Email, html, StringComparison.Ordinal);
        if (variant == "Page20CategoryOrder")
        {
            Assert.Equal(20, Regex.Matches(html, "<article").Count);
            using var next = await f.Browser.GetAsync(path + "&page=2"); Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            Assert.Single(Regex.Matches(await next.Content.ReadAsStringAsync(), "<article").Cast<Match>());
        }
        if (variant == "NoEligibleFutureNoBufferForm") { Assert.Contains("geen passende", html, StringComparison.Ordinal); Assert.DoesNotContain("JoinWaitlist", html, StringComparison.Ordinal); }
        if (variant == "NoSprint005EndpointsOrButtons")
        {
            Assert.DoesNotContain("Annuleren", html, StringComparison.Ordinal);
            foreach (var action in new[] { "Approve", "Refuse", "Cancel", "Promote" })
            { using var missing = await f.Browser.GetAsync("/EmployeeEnrolments/" + action + "/" + f.Moment.Id); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode); }
        }
        Assert.Equal(before, await f.DigestAsync());
    }
}

/// <summary>Gedeelde HTTP-fixture in dit gedeclareerde testbestand; iedere host bezit één eigen GUID-database.</summary>
internal sealed class EnrolmentHttpFixture : IAsyncDisposable
{
    public AccountWebApplicationFactory Factory { get; private init; } = null!;
    public IdentityHttpClient Browser { get; private init; } = null!;
    public TestIdentity Employee { get; private init; } = null!;
    public TestIdentity Owner { get; private init; } = null!;
    public TestIdentity Admin { get; private init; } = null!;
    public TrainingEntity Training { get; private set; } = null!;
    public TrainingMoment Moment { get; private set; } = null!;
    public Location Room { get; private set; } = null!;
    public EnrolmentFailureInterceptor Fault { get; private init; } = null!;

    public static async Task<EnrolmentHttpFixture> CreateAsync(int maximum = 2, bool external = false)
    {
        var factory = new AccountWebApplicationFactory();
        var fault = new EnrolmentFailureInterceptor();
        factory.ConfigureServicesBeforeStart = services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(factory.ConnectionString).AddInterceptors(fault));
        };
        var f = new EnrolmentHttpFixture { Factory = factory, Fault = fault,
            Employee = await factory.AddAccountAsync(RequestedAccountType.Employee, AccountApprovalStatus.Approved, [RoleNames.Medewerker]),
            Owner = await factory.AddAccountAsync(external ? RequestedAccountType.ExternalInstructor : RequestedAccountType.InternalInstructor,
                AccountApprovalStatus.Approved, external ? [RoleNames.Lesgever] : [RoleNames.Lesgever, RoleNames.Medewerker]),
            Admin = await factory.AddAccountAsync(null, AccountApprovalStatus.Approved, [RoleNames.Beheerder]),
            Browser = factory.CreateIdentityClient() };
        try
        {
            await factory.WithServicesAsync(async services =>
            {
                var db = services.GetRequiredService<ApplicationDbContext>();
                var owner = await db.Users.SingleAsync(x => x.Id == f.Owner.UserId);
                f.Training = await TrainingTestData.CreateTrainingAsync(services, owner, scope: TrainingAudienceScope.AllDepartments);
                f.Room = await TrainingTestData.CreateLocationAsync(services);
                f.Moment = await TrainingTestData.CreateMomentAsync(services, f.Training, location: f.Room, maximumParticipants: maximum);
            });
            await f.LoginAsync(f.Employee);
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }

    public async Task LoginAsync(TestIdentity identity)
    {
        using var response = await Browser.LoginAsync(identity);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
    public string MomentUrl => $"/EmployeeCatalogue/Moment/{Moment.Id}";
    public Dictionary<string, string> Form(bool join = false)
    {
        var fields = new Dictionary<string, string> { ["ExpectedTrainingVersion"] = Training.Version.ToString(),
            ["ExpectedMomentVersion"] = Moment.Version.ToString(), ["Motivation"] = "Mijn eigen motivatie" };
        if (join) fields["Consent"] = "true";
        return fields;
    }
    public Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> fields) =>
        Browser.PostFormAsync(path, fields, "/MyRegistrations");
    public static string Value(string html, string name)
    {
        foreach (Match tag in Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            if (Regex.Match(tag.Value, "\\bname=\"([^\"]*)\"").Groups[1].Value != name) continue;
            return WebUtility.HtmlDecode(Regex.Match(tag.Value, "\\bvalue=\"([^\"]*)\"").Groups[1].Value);
        }
        throw new InvalidOperationException("Het verwachte formulierinput ontbreekt: " + name);
    }
    public static string FormHtml(string html, string action)
    {
        var match = Regex.Match(html, "<form\\b[^>]*action=\"" + Regex.Escape(action) + "\"[^>]*>([\\s\\S]*?)</form>", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("Het bedoelde concrete formulier ontbreekt.");
        return match.Groups[1].Value;
    }
    public Task RemoveRoleAsync(string? userId = null) => MutateAsync(async db =>
    {
        var role = await db.Roles.Where(x => x.Name == RoleNames.Medewerker).Select(x => x.Id).SingleAsync();
        db.UserRoles.Remove(await db.UserRoles.SingleAsync(x => x.UserId == (userId ?? Employee.UserId) && x.RoleId == role));
    });
    public Task LinkHeadAsync(string? id = null) => MutateAsync(async db =>
        (await db.Departments.SingleAsync(x => x.Id == Employee.DepartmentId)).ResponsibleUserId = id ?? Owner.UserId);
    public async Task MutateAsync(Func<ApplicationDbContext, Task> mutation)
    {
        await Factory.WithServicesAsync(async services =>
        { var db = services.GetRequiredService<ApplicationDbContext>(); await mutation(db); await db.SaveChangesAsync(); });
    }
    public async Task<string> DigestAsync()
    {
        string result = "";
        await Factory.WithServicesAsync(async services => result = await TrainingTestData.DigestAsync(services));
        return result;
    }
    public async Task<WaitlistEntry> BufferAsync(TestIdentity? user = null, bool closed = false)
    {
        WaitlistEntry? row = null;
        await Factory.WithServicesAsync(async services => row = await TrainingTestData.CreateWaitlistEntryAsync(services, Training, closed,
            await services.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(x => x.Id == (user ?? Employee).UserId)));
        return row!;
    }
    public async Task<Registration> RegistrationAsync(RegistrationStatus status = RegistrationStatus.Requested, TestIdentity? user = null)
    {
        Registration? row = null;
        await Factory.WithServicesAsync(async services => row = await TrainingTestData.CreateRegistrationAsync(services, Moment, status,
            await services.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(x => x.Id == (user ?? Employee).UserId)));
        return row!;
    }
    public async ValueTask DisposeAsync() { Browser.Dispose(); await Factory.DisposeAsync(); }
}
