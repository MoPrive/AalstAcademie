using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Eigen SQLite-bestand, echte diensten en afzonderlijke scopes voor duurzame workflow-readbacks.</summary>
public sealed class ParticipationWorkflowFixture : IAsyncDisposable
{
    public FileSqliteTestDatabase Database { get; private set; } = null!;
    public IServiceProvider Services => Database.Services;
    public ApplicationDbContext Context => Database.Context;
    public FixedTimeProvider Clock => Services.GetRequiredService<FixedTimeProvider>();
    public ApplicationUser Admin { get; private set; } = null!;
    public ApplicationUser Owner { get; private set; } = null!;
    public ApplicationUser Employee { get; private set; } = null!;
    public AalstAcademie.Web.Models.Domain.Training Training { get; private set; } = null!;
    public TrainingMoment Moment { get; private set; } = null!;
    public Location Room { get; private set; } = null!;

    public static async Task<ParticipationWorkflowFixture> CreateAsync(int maximum = 2,
        IInterceptor? interceptor = null, bool demoEnabled = true)
    {
        var f = new ParticipationWorkflowFixture();
        f.Database = await FileSqliteTestDatabase.CreateAsync(interceptor: interceptor,
            configureServices: s => EnrolmentTestServices.Configure(s, demoEnabled));
        try
        {
            await TestAccountData.InitializeAsync(f.Services);
            f.Admin = await TestAccountData.CreateAdministratorAsync(f.Services);
            f.Owner = await TrainingTestData.CreateOwnerAsync(f.Services);
            f.Employee = await f.OtherEmployeeAsync();
            f.Training = await TrainingTestData.CreateTrainingAsync(f.Services, f.Owner,
                scope: TrainingAudienceScope.AllDepartments);
            f.Room = await TrainingTestData.CreateLocationAsync(f.Services);
            f.Moment = await TrainingTestData.CreateMomentAsync(f.Services, f.Training,
                location: f.Room, maximumParticipants: maximum);
            f.Context.ChangeTracker.Clear();
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    public Task<ApplicationUser> OtherEmployeeAsync() => TestAccountData.CreateAsync(Services,
        RequestedAccountType.Employee, AccountApprovalStatus.Approved, roles: [RoleNames.Medewerker]);
    public Task<WaitlistEntry> BufferAsync(ApplicationUser? user = null, bool closed = false) =>
        TrainingTestData.CreateWaitlistEntryAsync(Services, Training, closed, user ?? Employee);
    public Task<Registration> RegistrationAsync(RegistrationStatus status = RegistrationStatus.Requested,
        ApplicationUser? user = null, TrainingMoment? moment = null) =>
        TrainingTestData.CreateRegistrationAsync(Services, moment ?? Moment, status, user ?? Employee);
    public async Task<T> ExecuteAsync<T>(Func<IServiceProvider, Task<T>> command)
    {
        await using var scope = Database.CreateScope();
        return await command(scope.ServiceProvider);
    }
    public async Task RemoveRoleAsync(string userId, string roleName)
    {
        await MutateAsync(async db =>
        {
            var role = await db.Roles.Where(x => x.Name == roleName).Select(x => x.Id).SingleAsync();
            db.UserRoles.Remove(await db.UserRoles.SingleAsync(x => x.UserId == userId && x.RoleId == role));
        });
    }
    public async Task<TrainingMoment> OtherMomentAsync(DateOnly? date = null)
    {
        var days = await Context.TrainingMoments.AsNoTracking().Where(x => x.TrainingId == Training.Id).Select(x => x.Date).ToListAsync();
        return await TrainingTestData.CreateMomentAsync(Services, Training, location: Room,
            date: date ?? days.Max().AddDays(1), maximumParticipants: Moment.MaximumParticipants);
    }
    public async Task<T> ReadAsync<T>(Func<ApplicationDbContext, Task<T>> read)
    {
        await using var scope = Database.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }
    public async Task MutateAsync(Func<ApplicationDbContext, Task> action)
    {
        await using var scope = Database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await action(db); await db.SaveChangesAsync();
        Context.ChangeTracker.Clear();
    }
    public async Task<string> DigestAsync()
    {
        await using var scope = Database.CreateScope();
        return await TrainingTestData.DigestAsync(scope.ServiceProvider);
    }
    public ValueTask DisposeAsync() => Database.DisposeAsync();
}

/// <summary>Echte cookies, antiforgery en duurzame readbacks voor de drie beperkte workflowformulierfamilies.</summary>
internal static class ParticipationWorkflowHttpChecks
{
    public static async Task RunAsync(string kind, string variant)
    {
        await using var f = await AalstAcademie.Tests.Web.EnrolmentHttpFixture.CreateAsync(maximum: 1);
        var confirmed = variant.Contains("Confirmed", StringComparison.Ordinal);
        var row = await f.RegistrationAsync(confirmed ? RegistrationStatus.Confirmed : RegistrationStatus.Requested,
            variant is "OtherOwner404" or "ForeignDetail404" ? f.Owner : null);
        if (kind == "review") await f.LoginAsync(variant == "Employee403" ? f.Employee : variant.StartsWith("Owner", StringComparison.Ordinal) ? f.Owner : f.Admin);
        if (kind == "moment") await f.LoginAsync(variant == "Instructor403" ? f.Owner : f.Admin);
        if (variant == "RoleLossStillApprovedAllowed") await f.RemoveRoleAsync();
        // ForeignDetail uses a distinct owner's training, not a foreign participant in the same owned training.
        if (variant == "ForeignDetail404") await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).InstructorUserId = f.Employee.UserId);
        var getPath = kind == "review" ? "/RegistrationManagement/Details/" + row.Id :
            kind == "own" ? "/MyRegistrations/Cancel/" + row.Id : "/TrainingMoments/Cancel/" + f.Moment.Id;
        var postPath = kind == "review" ? "/RegistrationManagement/" + (variant.Contains("Refus", StringComparison.Ordinal) || variant.Contains("Reason", StringComparison.Ordinal) || variant.Contains("SqlFailure", StringComparison.Ordinal) ? "Refuse/" : "Confirm/") + row.Id : getPath;
        if (variant == "AdminAll")
        { using var page = await f.Browser.GetAsync("/RegistrationManagement"); Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode); Assert.Contains($"data-registration-id=\"{row.Id}\"", await page.Content.ReadAsStringAsync()); return; }
        if (variant.Contains("Html", StringComparison.Ordinal) || variant == "AdminConfirmationCountsAndWarning" || variant is "Employee403" or "Instructor403" or "ForeignDetail404" or "OtherOwner404")
        {
            if (variant == "ForeignDetail404") await f.LoginAsync(f.Owner);
            var beforeGet = await f.DigestAsync(); using var page = await f.Browser.GetAsync(getPath);
            Assert.Equal(variant is "Employee403" or "Instructor403" ? System.Net.HttpStatusCode.Forbidden :
                variant is "ForeignDetail404" or "OtherOwner404" ? System.Net.HttpStatusCode.NotFound : System.Net.HttpStatusCode.OK, page.StatusCode);
            if (page.IsSuccessStatusCode)
            {
                var html = await page.Content.ReadAsStringAsync(); Assert.Contains("<form", html); Assert.Contains("__RequestVerificationToken", html);
                Assert.Contains("ExpectedMomentVersion", html); Assert.Contains("type=\"submit\"", html);
                if (kind == "review") { Assert.Contains("for=\"DecisionReason\"", html); Assert.Contains("/RegistrationManagement/Confirm/", html); Assert.Contains("/RegistrationManagement/Refuse/", html); }
                if (kind == "moment") { Assert.Contains("volledige moment", html); Assert.Contains("wachtlijst", html); Assert.Contains("zaal", html); }
            }
            Assert.Equal(beforeGet, await f.DigestAsync()); return;
        }
        var form = new Dictionary<string,string> { ["ExpectedMomentVersion"] = f.Moment.Version.ToString() };
        if (kind != "moment") form["ExpectedRegistrationVersion"] = row.Version.ToString();
        if (postPath.Contains("Refuse", StringComparison.Ordinal)) form["DecisionReason"] = "Eigen behouden reden";
        if (variant.Contains("Stale", StringComparison.Ordinal)) form[kind == "moment" ? "ExpectedMomentVersion" : "ExpectedRegistrationVersion"] = Guid.NewGuid().ToString();
        if (variant == "MalformedVersion400") form["ExpectedRegistrationVersion"] = "geen-guid";
        if (variant.StartsWith("Overposted", StringComparison.Ordinal)) form[variant.Contains("Actor", StringComparison.Ordinal) ? "ApplicationUserId" : variant.Contains("Training", StringComparison.Ordinal) ? "TrainingId" : "Status"] = "onbevoegde-invoer";
        if (variant.StartsWith("SqlFailure", StringComparison.Ordinal))
        {
            if (kind != "moment") await f.BufferAsync(f.Owner);
            f.Fault.Arm(kind == "moment" ? "join" : "request");
        }
        if (variant == "NoRefillOtherMoment") await f.Factory.WithServicesAsync(async sp =>
            await TrainingTestData.CreateMomentAsync(sp, f.Training, location: f.Room, date: f.Moment.Date.AddDays(1)));
        var before = await f.DigestAsync();
        using var response = variant == "MissingAntiforgery400" ? await f.Browser.RawClient.PostAsync(postPath, new System.Net.Http.FormUrlEncodedContent(form)) : await f.PostAsync(postPath, form);
        var expected = variant.EndsWith("400", StringComparison.Ordinal) ? System.Net.HttpStatusCode.BadRequest :
            variant.Contains("Stale", StringComparison.Ordinal) ? System.Net.HttpStatusCode.Conflict :
            variant.StartsWith("SqlFailure", StringComparison.Ordinal) ? System.Net.HttpStatusCode.ServiceUnavailable :
            variant == "OwnerRefuseConfirmed403" ? System.Net.HttpStatusCode.Forbidden : System.Net.HttpStatusCode.Redirect;
        Assert.Equal(expected, response.StatusCode);
        if (expected != System.Net.HttpStatusCode.Redirect)
        {
            Assert.Equal(before, await f.DigestAsync());
            if (expected is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.ServiceUnavailable)
            {
                var html = await response.Content.ReadAsStringAsync();
                Assert.Equal(form["ExpectedMomentVersion"], AalstAcademie.Tests.Web.EnrolmentHttpFixture.Value(html, "ExpectedMomentVersion"));
                if (kind != "moment") Assert.Equal(form["ExpectedRegistrationVersion"], AalstAcademie.Tests.Web.EnrolmentHttpFixture.Value(html, "ExpectedRegistrationVersion"));
                if (form.TryGetValue("DecisionReason", out var reason)) Assert.Contains(reason, html);
                Assert.DoesNotContain("SQLite", html, StringComparison.OrdinalIgnoreCase);
            }
            if (variant.StartsWith("SqlFailure", StringComparison.Ordinal)) Assert.True(f.Fault.Triggered);
            return;
        }
        await f.Factory.WithServicesAsync(async sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            var saved = await db.Registrations.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(kind == "review" ? RegistrationStatus.Confirmed : RegistrationStatus.Cancelled, saved.Status);
            Assert.Equal(row.RequestedAtUtc, saved.RequestedAtUtc); Assert.Equal(row.Motivation, saved.Motivation); Assert.NotEqual(row.Version, saved.Version);
            if (kind == "moment")
            {
                Assert.Equal(TrainingMomentStatus.Cancelled, (await db.TrainingMoments.AsNoTracking().SingleAsync(x => x.Id == f.Moment.Id)).Status);
                var returned = await db.WaitlistEntries.AsNoTracking().SingleAsync(); Assert.Null(returned.ClosedAtUtc); Assert.Equal(row.RequestedAtUtc, returned.JoinedAtUtc);
                Assert.Equal(row.Motivation, returned.Motivation);
                if (variant == "NoRefillOtherMoment") Assert.Equal(1, await db.Registrations.CountAsync());
            }
            else Assert.Equal(0, await db.WaitlistEntries.CountAsync(x => x.ApplicationUserId == f.Employee.UserId));
        });
    }
}
