using System.Security.Claims;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Security;

/// <summary>Een actuele accountlezing met de opgeslagen beheerdersrol, zonder verouderde cookieclaims te vertrouwen.</summary>
public sealed record CurrentAccount(ApplicationUser User, bool IsAdministrator)
{
    /// <summary>Goedkeuring is een eigen businessstatus en volgt niet uit EmailConfirmed of een oude rolclaim.</summary>
    public bool IsApproved => User.AccountApprovalStatus == AccountApprovalStatus.Approved;
}

/// <summary>Leest een account opnieuw uit de database en deelt die lezing uitsluitend binnen één requestscope.</summary>
public sealed class CurrentAccountAccessor(
    IHttpContextAccessor httpContextAccessor,
    ApplicationDbContext database,
    DemoMode demoMode)
{
    // Geen singleton of statische cache: een nieuwe aanvraag moet een blokkering,
    // statuswijziging of ingetrokken beheerdersrol onmiddellijk uit de opslag zien.
    private string? cachedUserId;
    private Task<CurrentAccount?>? cachedRead;

    /// <summary>Leest de huidige HTTP-identiteit voor controllers, views en statuspagina's.</summary>
    public Task<CurrentAccount?> GetAsync(CancellationToken cancellationToken = default)
        => GetAsync(httpContextAccessor.HttpContext?.User, cancellationToken);

    /// <summary>Ondersteunt ook cookievalidatie, die plaatsvindt vóór HttpContext.User wordt toegewezen.</summary>
    public Task<CurrentAccount?> GetAsync(ClaimsPrincipal? principal, CancellationToken cancellationToken = default)
    {
        var userId = principal?.Identity?.IsAuthenticated == true
            ? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;
        if (string.IsNullOrWhiteSpace(userId))
            return Task.FromResult<CurrentAccount?>(null);

        // Bewaar ook een ontbrekend/ontoegankelijk account binnen dezelfde scope.
        // Een eventuele andere principal mag geen eerder resultaat overnemen.
        if (cachedRead is null || !string.Equals(cachedUserId, userId, StringComparison.Ordinal))
        {
            cachedUserId = userId;
            cachedRead = ReadAsync(userId, cancellationToken);
        }
        return cachedRead;
    }

    /// <summary>Leest de gebruiker en zijn huidige beheerderskoppeling; ontoegankelijke accounts leveren null op.</summary>
    private async Task<CurrentAccount?> ReadAsync(string userId, CancellationToken cancellationToken)
    {
        // AsNoTracking voorkomt dat een eerder door EF geladen gebruikersobject
        // de actuele opgeslagen toestand voor autorisatie overschrijft.
        var user = await database.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null || user.IsBlocked || (!demoMode.IsEnabled && user.DemoSeedKey is not null))
            return null;

        // Alleen de huidige opgeslagen roltoekenning verleent beheerdersrechten.
        var isAdministrator = await (
            from grant in database.UserRoles.AsNoTracking()
            join role in database.Roles.AsNoTracking() on grant.RoleId equals role.Id
            where grant.UserId == userId && role.Name == RoleNames.Beheerder
            select grant.UserId).AnyAsync(cancellationToken);
        return new CurrentAccount(user, isAdministrator);
    }
}
