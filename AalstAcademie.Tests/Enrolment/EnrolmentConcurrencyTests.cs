using System.Data.Common;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;
namespace AalstAcademie.Tests.Enrolment;

/// <summary>Twee fysieke verbindingen en een werkelijk vastgehouden writer-lock bewijzen alle relevante schrijfvolgordes.</summary>
[Collection("Revision02 provider races")]
public class EnrolmentConcurrencyTests
{
    [Theory]
    [InlineData("LastPlaceTwoActors")]
    [InlineData("DuplicateSameMomentTwoConnections")]
    [InlineData("SameActorDifferentMoments")]
    [InlineData("RequestFirstThenJoinSameTraining")]
    [InlineData("JoinFirstThenRequestSameTraining")]
    [InlineData("TwoBufferJoins")]
    [InlineData("WithdrawFirstThenRejoin")]
    [InlineData("OldWithdrawAfterRejoin")]
    [InlineData("RequestFirstThenMaximumLowered")]
    [InlineData("MaximumLoweredFirstThenRequest")]
    [InlineData("RequestFirstThenMaximumRaised")]
    [InlineData("MaximumRaisedFirstThenRequest")]
    [InlineData("RequestFirstThenRoomCapacityLowered")]
    [InlineData("RoomCapacityLoweredFirstThenRequest")]
    [InlineData("RequestFirstThenPlanningChanged")]
    [InlineData("PlanningChangedFirstThenRequest")]
    [InlineData("JoinFirstThenPlanningChanged")]
    [InlineData("PlanningChangedFirstThenJoin")]
    [InlineData("RequestFirstThenAudienceChanged")]
    [InlineData("AudienceChangedFirstThenRequest")]
    [InlineData("RoleRevokedWhileRequestWaitsForLock")]
    [InlineData("RequestFirstThenRoleRevoked")]
    [InlineData("ClockCrossesStartWhileRequestWaits")]
    [InlineData("ClockCrossesStartWhileJoinWaits")]
    [InlineData("RequestFirstThenPriceChange")]
    [InlineData("PriceChangeFirstThenRequest")]
    [InlineData("JoinFirstThenPriceChange")]
    [InlineData("PriceChangeFirstThenJoin")]
    [InlineData("RequestFirstThenMotivationRequiredChange")]
    [InlineData("MotivationRequiredChangeFirstThenRequest")]
    [InlineData("JoinFirstThenMotivationRequiredChange")]
    [InlineData("MotivationRequiredChangeFirstThenJoin")]
    [InlineData("EligibleWaiterWithdrawsFirstThenRequest")]
    [InlineData("RequestBeforeEligibleWaiterWithdraws")]
    [InlineData("WaiterRoleLossFirstThenRequest")]
    [InlineData("RequestBeforeWaiterRoleLoss")]
    [InlineData("JoinAfterDirectPlaceFreed")]
    [InlineData("FaultAfterActualInsertNoPartialRows")]
    public async Task Writer_orders(string variant)
    {
        var external = variant.Contains("Price", StringComparison.Ordinal);
        var lowered = variant.Contains("MaximumLowered", StringComparison.Ordinal);
        var roomChange = variant.Contains("RoomCapacity", StringComparison.Ordinal);
        await using var f = await EnrolmentRaceFixture.CreateAsync(maximum: lowered || roomChange ? 2 : 1, external: external);
        var identity = await f.IdentityDigestAsync();
        Task<TrainingOperationResult> Request(IServiceProvider sp) => f.Request(sp);
        Task<TrainingOperationResult> Join(IServiceProvider sp) => f.Join(sp);
        TrainingOperationResult[] result;

        if (variant is "LastPlaceTwoActors" or "DuplicateSameMomentTwoConnections" or "SameActorDifferentMoments" or "RequestFirstThenJoinSameTraining" or "FaultAfterActualInsertNoPartialRows")
        {
            TrainingMoment? other = variant == "SameActorDifferentMoments" ? await f.OtherMomentAsync() : null;
            f.Gate.FailAfterWrite = variant == "FaultAfterActualInsertNoPartialRows";
            result = await f.Race(Request, sp => variant == "RequestFirstThenJoinSameTraining" ? Join(sp) :
                f.Request(sp, variant is "LastPlaceTwoActors" or "FaultAfterActualInsertNoPartialRows" ? f.Other.Id : f.Employee.Id, other));
            Assert.Equal(f.Gate.FailAfterWrite ? TrainingOperationStatus.StorageUnavailable : TrainingOperationStatus.Success, result[0].Status);
            Assert.Equal(f.Gate.FailAfterWrite ? TrainingOperationStatus.Success : TrainingOperationStatus.Conflict, result[1].Status);
            await f.Read(async db =>
            {
                var row = await db.Registrations.AsNoTracking().SingleAsync();
                Assert.Equal(f.Gate.FailAfterWrite ? f.Other.Id : f.Employee.Id, row.ApplicationUserId);
                Assert.Equal(f.Moment.Id, row.TrainingMomentId); Assert.Equal(RegistrationStatus.Requested, row.Status);
                Assert.NotEqual(Guid.Empty, row.Version); Assert.Equal("Eigen race-motivatie", row.Motivation);
                Assert.Empty(await db.WaitlistEntries.ToListAsync());
            });
        }
        else if (variant is "JoinFirstThenRequestSameTraining" or "TwoBufferJoins" or "WithdrawFirstThenRejoin" or "OldWithdrawAfterRejoin")
        {
            await f.OccupyAsync();
            if (variant == "JoinFirstThenRequestSameTraining")
            {
                var other = await f.OtherMomentAsync(); result = await f.Race(Join, sp => f.Request(sp, moment: other));
                Assert.Equal(TrainingOperationStatus.Success, result[0].Status); Assert.Equal(TrainingOperationStatus.Conflict, result[1].Status);
            }
            else if (variant == "TwoBufferJoins")
            { result = await f.Race(Join, sp => f.Join(sp, f.Other.Id)); Assert.All(result, x => Assert.True(x.Succeeded)); }
            else
            {
                var old = await f.BufferAsync(f.Employee, closed: variant == "OldWithdrawAfterRejoin");
                Task<TrainingOperationResult> Withdraw(IServiceProvider sp) => sp.GetRequiredService<EmployeeEnrolmentService>()
                    .WithdrawAsync(f.Employee.Id, old.Id, new() { ExpectedVersion = old.Version });
                result = variant == "WithdrawFirstThenRejoin" ? await f.Race(Withdraw, Join) : await f.Race(Join, Withdraw);
                Assert.True(result[0].Succeeded); Assert.Equal(variant == "WithdrawFirstThenRejoin" ? TrainingOperationStatus.Success : TrainingOperationStatus.Conflict, result[1].Status);
                await f.Read(async db =>
                {
                    var history = await db.WaitlistEntries.AsNoTracking().SingleAsync(x => x.Id == old.Id);
                    Assert.NotNull(history.ClosedAtUtc); Assert.Equal(old.JoinedAtUtc, history.JoinedAtUtc); Assert.Equal(old.Motivation, history.Motivation);
                    var active = await db.WaitlistEntries.AsNoTracking().SingleAsync(x => x.ClosedAtUtc == null);
                    Assert.NotEqual(old.Id, active.Id); Assert.NotEqual(old.Version, active.Version); Assert.Equal(f.Employee.Id, active.ApplicationUserId);
                });
            }
            await f.Read(async db =>
            {
                Assert.Equal(1, await db.Registrations.CountAsync());
                Assert.Equal(f.Owner.Id, (await db.Registrations.SingleAsync()).ApplicationUserId);
                Assert.Equal(variant == "TwoBufferJoins" ? 2 : 1, await db.WaitlistEntries.CountAsync(x => x.ClosedAtUtc == null));
            });
        }
        else if (variant.Contains("Maximum", StringComparison.Ordinal) || roomChange || variant.Contains("Planning", StringComparison.Ordinal) || variant.Contains("Audience", StringComparison.Ordinal))
        {
            var joining = variant.Contains("Join", StringComparison.Ordinal);
            if (joining) await f.BufferAsync(f.Other); // E=1 reserveert de ene fysieke plaats zonder Registration-historie.
            var excludedDepartment = 0;
            if (variant.Contains("Audience", StringComparison.Ordinal))
                await f.Read(async db => excludedDepartment = await db.Departments.Where(x => x.Id != f.Employee.DepartmentId).Select(x => x.Id).FirstAsync());
            var participantFirst = variant.StartsWith("RequestFirst", StringComparison.Ordinal) || variant.StartsWith("JoinFirst", StringComparison.Ordinal);
            Task<TrainingOperationResult> Participant(IServiceProvider sp) => joining ? Join(sp) : Request(sp);
            Task<TrainingOperationResult> Change(IServiceProvider sp)
            {
                if (roomChange) return sp.GetRequiredService<LocationManagementService>().UpdateAsync(f.Admin.Id,
                    new(f.Room.Id, f.Room.Version, new(f.Room.Name, f.Room.Address, 2)));
                if (variant.Contains("Maximum", StringComparison.Ordinal)) return sp.GetRequiredService<TrainingMomentManagementService>()
                    .ChangeCapacityAsync(f.Owner.Id, new(f.Moment.Id, f.Moment.Version, lowered ? 1 : 2));
                if (variant.Contains("Audience", StringComparison.Ordinal)) return sp.GetRequiredService<TrainingManagementService>()
                    .AssignAudienceAsync(f.Admin.Id, new(f.Training.Id, f.Training.Version, TrainingAudienceScope.SelectedDepartments, [excludedDepartment]));
                return sp.GetRequiredService<TrainingMomentManagementService>().UpdateAsync(f.Owner.Id,
                    new(f.Moment.Id, f.Moment.Version, new(f.Moment.Date.AddDays(2), f.Moment.StartTime, f.Moment.EndTime, f.Room.Id)));
            }
            result = participantFirst ? await f.Race(Participant, Change) : await f.Race(Change, Participant);
            Assert.True(result[0].Succeeded);
            var audience = variant.Contains("Audience", StringComparison.Ordinal);
            var planning = variant.Contains("Planning", StringComparison.Ordinal);
            var expectedSecond = roomChange || participantFirst && (!planning || joining)
                ? TrainingOperationStatus.Success : !participantFirst && audience ? TrainingOperationStatus.Forbidden : TrainingOperationStatus.Conflict;
            Assert.Equal(expectedSecond, result[1].Status);
            await f.Read(async db =>
            {
                Assert.Equal(joining ? 0 : participantFirst || roomChange ? 1 : 0, await db.Registrations.CountAsync());
                Assert.Equal(joining ? participantFirst ? 2 : 1 : 0, await db.WaitlistEntries.CountAsync(x => x.ClosedAtUtc == null));
                var moment = await db.TrainingMoments.SingleAsync(x => x.Id == f.Moment.Id); var room = await db.Locations.SingleAsync(x => x.Id == f.Room.Id);
                Assert.True(moment.MaximumParticipants <= room.MaximumCapacity);
                Assert.True(await db.Registrations.CountAsync(x => x.Status == RegistrationStatus.Requested || x.Status == RegistrationStatus.Confirmed) <= moment.MaximumParticipants);
                if (planning) Assert.Equal(participantFirst && !joining ? f.Moment.Date : f.Moment.Date.AddDays(2), moment.Date);
                if (audience)
                {
                    Assert.Equal(TrainingAudienceScope.SelectedDepartments, (await db.Trainings.SingleAsync()).AudienceScope);
                    Assert.Equal(excludedDepartment, (await db.TrainingDepartments.SingleAsync()).DepartmentId);
                    Assert.NotEqual(f.Employee.DepartmentId, excludedDepartment);
                }
            });
        }
        else if (variant.Contains("RoleRevoked", StringComparison.Ordinal))
        {
            Task<TrainingOperationResult> Revoke(IServiceProvider sp) => f.Revoke(sp, f.Employee.Id, RoleNames.Medewerker);
            var firstRequest = variant == "RequestFirstThenRoleRevoked";
            result = firstRequest ? await f.Race(Request, Revoke) : await f.Race(Revoke, Request);
            Assert.True(result[0].Succeeded); Assert.Equal(firstRequest ? TrainingOperationStatus.Success : TrainingOperationStatus.Forbidden, result[1].Status);
            await f.Read(async db => Assert.Equal(firstRequest ? 1 : 0, await db.Registrations.CountAsync()));
        }
        else if (variant.StartsWith("ClockCrosses", StringComparison.Ordinal))
        {
            var joining = variant.Contains("Join", StringComparison.Ordinal); if (joining) await f.BufferAsync(f.Other);
            var start = f.Services.GetRequiredService<TrainingSchedule>().Validate(f.Moment.Date, f.Moment.StartTime, f.Moment.EndTime).Value!.StartUtc;
            f.Clock.UtcNow = start.AddMinutes(-1); var before = await f.DigestAsync();
            result = await f.Race(sp => f.Write(sp, async db =>
                { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Departments SET Name = Name WHERE Id = {f.Employee.DepartmentId}"); }),
                sp => joining ? Join(sp) : Request(sp), whileLocked: () => f.Clock.UtcNow = start);
            Assert.True(result[0].Succeeded); Assert.Equal(TrainingOperationStatus.Conflict, result[1].Status);
            Assert.Equal(before, await f.DigestAsync());
        }
        else if (variant.Contains("PriceChange", StringComparison.Ordinal) || variant.Contains("MotivationRequiredChange", StringComparison.Ordinal))
        {
            var joining = variant.Contains("Join", StringComparison.Ordinal); if (joining) await f.OccupyAsync();
            var participantFirst = variant.StartsWith("RequestFirst", StringComparison.Ordinal) || variant.StartsWith("JoinFirst", StringComparison.Ordinal);
            Task<TrainingOperationResult> Participant(IServiceProvider sp) => joining ? Join(sp) : Request(sp);
            Task<TrainingOperationResult> Terms(IServiceProvider sp) => sp.GetRequiredService<TrainingManagementService>().UpdateAsync(f.Owner.Id,
                new(f.Training.Id, f.Training.Version, new(f.Training.Title, f.Training.Description, f.Training.CategoryId,
                    external ? f.Training.ExternalTotalPriceEuros + 1m : null, !external)));
            // Een volle Join-keuze heeft al historie. De vroege guard-read houdt de echte writer-lock vast
            // wanneer een eerste voorwaardenwijziging daardoor terecht geen UPDATE mag uitvoeren.
            result = participantFirst ? await f.Race(Participant, Terms) : await f.Race(Terms, Participant, pauseAtGuard: joining);
            Assert.Equal(participantFirst ? TrainingOperationStatus.Success : joining ? TrainingOperationStatus.Conflict : TrainingOperationStatus.Success, result[0].Status);
            Assert.Equal(participantFirst ? TrainingOperationStatus.Conflict : joining ? TrainingOperationStatus.Success : TrainingOperationStatus.Conflict, result[1].Status);
            await f.Read(async db =>
            {
                var t = await db.Trainings.SingleAsync(); var changed = !participantFirst && !joining;
                Assert.Equal(changed && !external, t.RequiresMotivation);
                Assert.Equal(changed && external ? f.Training.ExternalTotalPriceEuros + 1m : f.Training.ExternalTotalPriceEuros, t.ExternalTotalPriceEuros);
                Assert.Equal(joining || participantFirst ? 1 : 0, await db.Registrations.CountAsync());
                Assert.Equal(joining ? 1 : 0, await db.WaitlistEntries.CountAsync(x => x.ApplicationUserId == f.Employee.Id));
            });
        }
        else if (variant.Contains("EligibleWaiter", StringComparison.Ordinal) || variant.Contains("WaiterRoleLoss", StringComparison.Ordinal))
        {
            var buffer = await f.BufferAsync(f.Other); var beforeRequest = variant.StartsWith("RequestBefore", StringComparison.Ordinal);
            Task<TrainingOperationResult> ReleasePriority(IServiceProvider sp) => variant.Contains("RoleLoss", StringComparison.Ordinal)
                ? f.Revoke(sp, f.Other.Id, RoleNames.Medewerker)
                : sp.GetRequiredService<EmployeeEnrolmentService>().WithdrawAsync(f.Other.Id, buffer.Id, new() { ExpectedVersion = buffer.Version });
            result = beforeRequest ? await f.Race(Request, ReleasePriority, pauseAtGuard: true) : await f.Race(ReleasePriority, Request);
            Assert.Equal(beforeRequest ? TrainingOperationStatus.Conflict : TrainingOperationStatus.Success, result[0].Status); Assert.True(result[1].Succeeded);
            await f.Read(async db =>
            {
                Assert.Equal(beforeRequest ? 0 : 1, await db.Registrations.CountAsync());
                var old = await db.WaitlistEntries.SingleAsync(); Assert.Equal(buffer.JoinedAtUtc, old.JoinedAtUtc); Assert.Equal(buffer.Motivation, old.Motivation);
                Assert.Equal(!variant.Contains("RoleLoss", StringComparison.Ordinal), old.ClosedAtUtc is not null);
            });
        }
        else if (variant == "JoinAfterDirectPlaceFreed")
        {
            var occupied = await f.OccupyAsync();
            result = await f.Race(sp => f.Write(sp, async db =>
                { var row = await db.Registrations.SingleAsync(x => x.Id == occupied.Id); row.Status = RegistrationStatus.Cancelled; row.Version = Guid.NewGuid(); }), Join);
            Assert.True(result[0].Succeeded); Assert.Equal(TrainingOperationStatus.Conflict, result[1].Status);
            await f.Read(async db => { Assert.Empty(await db.WaitlistEntries.ToListAsync()); Assert.Equal(RegistrationStatus.Cancelled, (await db.Registrations.SingleAsync()).Status); });
        }
        else throw new InvalidOperationException("Een geplande racevariant mist haar concrete gedrag.");
        if (!variant.Contains("Role", StringComparison.Ordinal)) Assert.Equal(identity, await f.IdentityDigestAsync());
    }
}

/// <summary>Pauzeert SQL binnen een echte niet-deferred transactie; een geweigerde eerste writer pauzeert expliciet op zijn guard-read.</summary>
internal sealed class EnrolmentSqlGate : DbCommandInterceptor
{
    private int armed;
    private bool guard;
    public bool FailAfterWrite { get; set; }
    public bool SawWrite { get; private set; }
    public bool SawLockedGuard { get; private set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Arm(bool pauseAtGuard) { guard = pauseAtGuard; Interlocked.Exchange(ref armed, 1); }
    private async Task After(DbCommand command)
    {
        var write = command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal) || command.CommandText.Contains("UPDATE ", StringComparison.Ordinal) || command.CommandText.Contains("DELETE FROM", StringComparison.Ordinal);
        if (command.Transaction is null || !(guard || write) || Interlocked.Exchange(ref armed, 0) != 1) return;
        SawWrite = write; SawLockedGuard = !write; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (FailAfterWrite) throw new SqliteException("Geïsoleerde fout na de echte race-insert.", 1);
    }
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
    { try { await After(command); return result; } catch { await result.DisposeAsync(); throw; } }
    public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken ct = default)
    { await After(command); return result; }
}

/// <summary>Begrensde gezamenlijke fixture in dit vooraf gedeclareerde bestand; geen gedeelde DbContext of gebruikersdatabase.</summary>
internal sealed class EnrolmentRaceFixture : IAsyncDisposable
{
    public FileSqliteTestDatabase Database { get; private init; } = null!;
    public EnrolmentSqlGate Gate { get; private init; } = null!;
    public IServiceProvider Services => Database.Services;
    public FixedTimeProvider Clock => (FixedTimeProvider)Services.GetRequiredService<TimeProvider>();
    public ApplicationUser Employee { get; private init; } = null!;
    public ApplicationUser Other { get; private init; } = null!;
    public ApplicationUser Owner { get; private init; } = null!;
    public ApplicationUser Admin { get; private init; } = null!;
    public TrainingEntity Training { get; private init; } = null!;
    public TrainingMoment Moment { get; private init; } = null!;
    public Location Room { get; private init; } = null!;
    public static async Task<EnrolmentRaceFixture> CreateAsync(int maximum = 1, bool external = false)
    {
        var gate = new EnrolmentSqlGate(); var database = await FileSqliteTestDatabase.CreateAsync(interceptor: gate, configureServices: s => EnrolmentTestServices.Configure(s));
        try
        {
            var sp = database.Services; await TestAccountData.InitializeAsync(sp);
            var employee = await TestAccountData.CreateAsync(sp, RequestedAccountType.Employee, AccountApprovalStatus.Approved, roles: [RoleNames.Medewerker]);
            var other = await TestAccountData.CreateAsync(sp, RequestedAccountType.Employee, AccountApprovalStatus.Approved, roles: [RoleNames.Medewerker]);
            var owner = await TrainingTestData.CreateOwnerAsync(sp, external); var admin = await TestAccountData.CreateAdministratorAsync(sp);
            var training = await TrainingTestData.CreateTrainingAsync(sp, owner, scope: TrainingAudienceScope.AllDepartments);
            var room = await TrainingTestData.CreateLocationAsync(sp); var moment = await TrainingTestData.CreateMomentAsync(sp, training, location: room, maximumParticipants: maximum);
            database.Context.ChangeTracker.Clear();
            return new() { Database = database, Gate = gate, Employee = employee, Other = other, Owner = owner, Admin = admin, Training = training, Room = room, Moment = moment };
        }
        catch { await database.DisposeAsync(); throw; }
    }
    public Task<TrainingOperationResult> Request(IServiceProvider sp, string? actor = null, TrainingMoment? moment = null) =>
        sp.GetRequiredService<EmployeeEnrolmentService>().RequestAsync(actor ?? Employee.Id, (moment ?? Moment).Id,
            new() { ExpectedTrainingVersion = Training.Version, ExpectedMomentVersion = (moment ?? Moment).Version, Motivation = "Eigen race-motivatie" });
    public Task<TrainingOperationResult> Join(IServiceProvider sp, string? actor = null) =>
        sp.GetRequiredService<EmployeeEnrolmentService>().JoinAsync(actor ?? Employee.Id, Moment.Id,
            new() { ExpectedTrainingVersion = Training.Version, ExpectedMomentVersion = Moment.Version, Motivation = "Eigen race-motivatie", Consent = true });
    public Task<TrainingMoment> OtherMomentAsync() => TrainingTestData.CreateMomentAsync(Services, Training, location: Room, date: Moment.Date.AddDays(1), maximumParticipants: Moment.MaximumParticipants);
    public Task<Registration> OccupyAsync() => TrainingTestData.CreateRegistrationAsync(Services, Moment, applicant: Owner);
    public Task<WaitlistEntry> BufferAsync(ApplicationUser user, bool closed = false) => TrainingTestData.CreateWaitlistEntryAsync(Services, Training, closed, user);
    public async Task Read(Func<ApplicationDbContext, Task> read) { await using var scope = Database.CreateScope(); await read(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()); }
    public async Task<string> DigestAsync() { await using var scope = Database.CreateScope(); return await TrainingTestData.DigestAsync(scope.ServiceProvider); }
    public async Task<string> IdentityDigestAsync() { await using var scope = Database.CreateScope(); return await TrainingTestData.DigestAsync(scope.ServiceProvider, false); }
    public Task<TrainingOperationResult> Write(IServiceProvider sp, Func<ApplicationDbContext, Task> mutation) => sp.GetRequiredService<TrainingWriteTransaction>().ExecuteAsync(async _ =>
    { var db = sp.GetRequiredService<ApplicationDbContext>(); await mutation(db); await db.SaveChangesAsync(); return new(TrainingOperationStatus.Success); });
    public Task<TrainingOperationResult> Revoke(IServiceProvider sp, string user, string role) => Write(sp, async db =>
    { var id = await db.Roles.Where(x => x.Name == role).Select(x => x.Id).SingleAsync(); await db.UserRoles.Where(x => x.UserId == user && x.RoleId == id).ExecuteDeleteAsync(); });
    public async Task<TrainingOperationResult[]> Race(Func<IServiceProvider, Task<TrainingOperationResult>> first,
        Func<IServiceProvider, Task<TrainingOperationResult>> second, bool pauseAtGuard = false, Action? whileLocked = null)
    {
        await using var a = Database.CreateScope(); await using var b = Database.CreateScope();
        var ca = a.ServiceProvider.GetRequiredService<ApplicationDbContext>(); var cb = b.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await ca.Database.OpenConnectionAsync(); await cb.Database.OpenConnectionAsync();
        Assert.NotSame(ca.Database.GetDbConnection(), cb.Database.GetDbConnection()); Assert.Equal(ca.Database.GetDbConnection().DataSource, cb.Database.GetDbConnection().DataSource);
        Gate.Arm(pauseAtGuard); var firstTask = Task.Run(() => first(a.ServiceProvider)); Task<TrainingOperationResult>? secondTask = null;
        try
        {
            await Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            secondTask = Task.Run(() => { started.TrySetResult(); return second(b.ServiceProvider); });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(150);
            Assert.False(secondTask.IsCompleted); Assert.True(pauseAtGuard ? Gate.SawLockedGuard || Gate.SawWrite : Gate.SawWrite); whileLocked?.Invoke();
        }
        finally
        {
            // Iedere gestarte writer eindigt vóór scope/provider/disposal, ook na een assertionfailure.
            Gate.Release.TrySetResult(); var startedTasks = secondTask is null ? new[] { firstTask } : new[] { firstTask, secondTask };
            await Task.WhenAll(startedTasks).WaitAsync(TimeSpan.FromSeconds(15));
        }
        return await Task.WhenAll(firstTask, secondTask!);
    }
    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
