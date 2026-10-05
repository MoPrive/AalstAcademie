using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Accounts;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Beperkte eigenprofielactie: alleen de manager-FK en Identity-concurrencytoken mogen veranderen.</summary>
public sealed class AccountManagerService(ApplicationDbContext context, EmployeeAccessReader access,
    AccountManagerChoices choices, TrainingWriteTransaction transaction)
{
    public async Task<AccountManagerViewModel?> GetAsync(string? actorId, CancellationToken ct = default)
    {
        var actor = await access.ReadApprovedAsync(actorId, ct);
        if (actor?.AccountType is not (RequestedAccountType.Employee or RequestedAccountType.InternalInstructor)) return null;
        var user = await context.Users.AsNoTracking().SingleAsync(x => x.Id == actorId, ct);
        return new() { DisplayName = actor.DisplayName, CurrentManagerName = actor.ManagerName,
            Input = new() { ManagerUserId = user.ManagerUserId, ExpectedConcurrencyStamp = user.ConcurrencyStamp }, Choices = await choices.GetAsync(ct) };
    }

    public Task<TrainingOperationResult> ChangeAsync(string? actorId, AccountManagerInput input, CancellationToken ct = default) =>
        transaction.ExecuteAsync(async token =>
        {
            var actor = await access.ReadApprovedAsync(actorId, token);
            if (actor?.AccountType is not (RequestedAccountType.Employee or RequestedAccountType.InternalInstructor)) return new(TrainingOperationStatus.Forbidden);
            var managerId = string.IsNullOrWhiteSpace(input.ManagerUserId) ? null : input.ManagerUserId.Trim();
            if (string.IsNullOrWhiteSpace(input.ExpectedConcurrencyStamp)) return new(TrainingOperationStatus.Invalid);
            if (!await choices.IsAllowedAsync(managerId, token)) return new(TrainingOperationStatus.Invalid, Errors:
                new Dictionary<string, string[]> { ["ManagerUserId"] = ["Kies een huidige afdelingsverantwoordelijke of Geen."] });
            var user = await context.Users.AsNoTracking().SingleAsync(x => x.Id == actorId, token);
            if (user.ConcurrencyStamp != input.ExpectedConcurrencyStamp) return new(TrainingOperationStatus.Conflict);
            // Update alleen de twee toegestane kolommen. SecurityStamp, rollen en reviewgegevens blijven buiten deze actie.
            var count = await context.Users.Where(x => x.Id == actorId && x.ConcurrencyStamp == input.ExpectedConcurrencyStamp)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ManagerUserId, managerId)
                    .SetProperty(x => x.ConcurrencyStamp, Guid.NewGuid().ToString()), token);
            return new(count == 1 ? TrainingOperationStatus.Success : TrainingOperationStatus.Conflict);
        }, ct);
}
