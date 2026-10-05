using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Accounts;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Mini-beheeroverzicht: bestaande afdelingen en geschikte persoonlijke accounts, zonder contact- of rechtengegevens.</summary>
public sealed class DepartmentResponsibleQueries(ApplicationDbContext context, TrainingAccessReader access, DemoMode demoMode)
{
    internal IQueryable<ApplicationUser> Candidates => context.Users.AsNoTracking().Where(x => !x.IsBlocked &&
        x.AccountApprovalStatus == AccountApprovalStatus.Approved && (demoMode.IsEnabled || x.DemoSeedKey == null) &&
        x.FirstName != null && x.FirstName.Trim() != "" && x.LastName != null && x.LastName.Trim() != "");

    public async Task<DepartmentResponsiblesViewModel?> GetAsync(string? actorId, CancellationToken ct = default)
    {
        if ((await access.ReadActorAsync(actorId, ct))?.IsAdministrator != true) return null;
        var departments = await context.Departments.AsNoTracking().Include(x => x.ResponsibleUser).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        var candidates = await Candidates.Select(x => new { x.Id, x.FirstName, x.LastName }).ToListAsync(ct);
        return new() { Departments = departments.Select(x => new DepartmentResponsibleRow(x.Id, x.Name,
            x.ResponsibleUser is null ? null : $"{x.ResponsibleUser.FirstName} {x.ResponsibleUser.LastName}".Trim(),
            new() { ResponsibleUserId = x.ResponsibleUserId, ExpectedResponsibleUserId = x.ResponsibleUserId })).ToArray(),
            Candidates = new[] { new AccountManagerChoice(null, "Geen") }.Concat(candidates.Select(x => new AccountManagerChoice(x.Id,
                $"{x.FirstName} {x.LastName}".Trim())).OrderBy(x => x.DisplayName, StringComparer.Ordinal).ThenBy(x => x.UserId, StringComparer.Ordinal)).ToArray() };
    }
}
