using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Enrolment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Iedere fixture bezit één nieuw filedoel; iedere mutation/readbackscope heeft een eigen fysieke verbinding.</summary>
public sealed class EnrolmentFixture : IAsyncDisposable
{
    public FileSqliteTestDatabase Database { get; private set; } = null!;
    public IServiceProvider Services => Database.Services;
    public ApplicationDbContext Context => Database.Context;
    public FixedTimeProvider Clock => Services.GetRequiredService<FixedTimeProvider>();
    public ApplicationUser Employee { get; private set; } = null!;
    public ApplicationUser Owner { get; private set; } = null!;
    public ApplicationUser Admin { get; private set; } = null!;
    public AalstAcademie.Web.Models.Domain.Training Training { get; private set; } = null!;
    public TrainingMoment Moment { get; private set; } = null!;
    public Location Room { get; private set; } = null!;
    public EmployeeEnrolmentService Enrolments => Services.GetRequiredService<EmployeeEnrolmentService>();

    public static async Task<EnrolmentFixture> CreateAsync(int maximum = 2, bool external = false,
        bool demoEnabled = true, EnrolmentFailureInterceptor? interceptor = null, TimeProvider? clock = null)
    {
        var fixture = new EnrolmentFixture();
        fixture.Database = await FileSqliteTestDatabase.CreateAsync(interceptor: interceptor,
            configureServices: s => { EnrolmentTestServices.Configure(s, demoEnabled);
                if (clock is not null) { s.RemoveAll<TimeProvider>(); s.AddSingleton(clock); } });
        try
        {
            await TestAccountData.InitializeAsync(fixture.Services);
            fixture.Admin = await TestAccountData.CreateAdministratorAsync(fixture.Services);
            fixture.Admin.FirstName = "Demi"; fixture.Admin.LastName = "Demo";
            await fixture.Context.SaveChangesAsync();
            fixture.Employee = await TestAccountData.CreateAsync(fixture.Services, RequestedAccountType.Employee,
                AccountApprovalStatus.Approved, roles: [RoleNames.Medewerker]);
            fixture.Owner = await TrainingTestData.CreateOwnerAsync(fixture.Services, external);
            fixture.Training = await TrainingTestData.CreateTrainingAsync(fixture.Services, fixture.Owner,
                scope: TrainingAudienceScope.AllDepartments);
            fixture.Room = await TrainingTestData.CreateLocationAsync(fixture.Services);
            fixture.Moment = await TrainingTestData.CreateMomentAsync(fixture.Services, fixture.Training,
                location: fixture.Room, maximumParticipants: maximum);
            fixture.Context.ChangeTracker.Clear();
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public EmployeeRequestInput Request(string? motivation = null) => new()
    { ExpectedTrainingVersion = Training.Version, ExpectedMomentVersion = Moment.Version, Motivation = motivation };
    public EmployeeWaitlistInput Join(string? motivation = null, bool consent = true) => new()
    { ExpectedTrainingVersion = Training.Version, ExpectedMomentVersion = Moment.Version, Motivation = motivation, Consent = consent };
    public async Task<TrainingMoment> OtherMomentAsync()
    {
        var dates = await Context.TrainingMoments.AsNoTracking().Where(x => x.TrainingId == Training.Id).Select(x => x.Date).ToListAsync();
        return await TrainingTestData.CreateMomentAsync(Services, Training, location: Room,
            date: dates.Max().AddDays(1), maximumParticipants: Moment.MaximumParticipants);
    }
    public Task<Registration> RegistrationAsync(RegistrationStatus status = RegistrationStatus.Requested,
        ApplicationUser? user = null, TrainingMoment? moment = null) =>
        TrainingTestData.CreateRegistrationAsync(Services, moment ?? Moment, status, user ?? Employee);
    public Task<WaitlistEntry> BufferAsync(ApplicationUser? user = null, bool closed = false) =>
        TrainingTestData.CreateWaitlistEntryAsync(Services, Training, closed, user ?? Employee);
    public Task<ApplicationUser> OtherEmployeeAsync() => TestAccountData.CreateAsync(Services,
        RequestedAccountType.Employee, AccountApprovalStatus.Approved, roles: [RoleNames.Medewerker]);

    public async Task MutateAsync(Func<ApplicationDbContext, Task> action)
    {
        await using var scope = Database.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await action(db); await db.SaveChangesAsync();
    }
    public async Task<string> DigestAsync()
    {
        await using var scope = Database.CreateScope();
        return await TrainingTestData.DigestAsync(scope.ServiceProvider);
    }
    public async Task LinkHeadAsync(string? userId = null)
    {
        await MutateAsync(async db => (await db.Departments.SingleAsync(x => x.Id == Employee.DepartmentId)).ResponsibleUserId = userId ?? Owner.Id);
    }
    public async Task RemoveEmployeeRoleAsync(string? userId = null)
    {
        await MutateAsync(async db =>
        {
            var roleId = await db.Roles.Where(x => x.Name == RoleNames.Medewerker).Select(x => x.Id).SingleAsync();
            var grant = await db.UserRoles.SingleAsync(x => x.UserId == (userId ?? Employee.Id) && x.RoleId == roleId);
            db.UserRoles.Remove(grant);
        });
    }
    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
