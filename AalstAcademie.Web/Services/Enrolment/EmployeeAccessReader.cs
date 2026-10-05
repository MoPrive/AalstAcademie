using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Models.Identity;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Enrolment;

/// <summary>Actuele opslag beslist over deelname. Cookies en getrackte entities leveren geen schrijfautorisatie.</summary>
public sealed class EmployeeAccessReader(ApplicationDbContext context, DemoMode demoMode)
{
    public async Task<EmployeeActor?> ReadApprovedAsync(string? id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var user = await context.Users.AsNoTracking().Include(x => x.Department).Include(x => x.Manager)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is null || user.IsBlocked || user.AccountApprovalStatus != AccountApprovalStatus.Approved ||
            (!demoMode.IsEnabled && user.DemoSeedKey is not null)) return null;
        return new(user.Id, user.DepartmentId, user.RequestedAccountType,
            $"{user.FirstName} {user.LastName}".Trim(), user.Email, user.PhoneNumber, user.Department?.Name,
            user.Manager is null ? null : $"{user.Manager.FirstName} {user.Manager.LastName}".Trim());
    }

    /// <summary>Nieuwe deelname vergt Medewerker en een bestaande geverifieerde werkafdeling; een manager is optioneel.</summary>
    public async Task<EmployeeActor?> ReadEmployeeAsync(string? id, CancellationToken ct = default)
    {
        var actor = await ReadApprovedAsync(id, ct);
        if (actor?.DepartmentId is null || actor.DepartmentName is null) return null;
        var employee = await (from grant in context.UserRoles.AsNoTracking()
            join role in context.Roles.AsNoTracking() on grant.RoleId equals role.Id
            where grant.UserId == id && role.NormalizedName == RoleNames.Medewerker.ToUpperInvariant()
            select grant.UserId).AnyAsync(ct);
        return employee ? actor : null;
    }
}
