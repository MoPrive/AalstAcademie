using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Accounts;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Een nieuwe managerkeuze bestaat uit daadwerkelijk gekoppelde heads, ongeacht hun afdeling of rollen.</summary>
public sealed class AccountManagerChoices(ApplicationDbContext context)
{
    public async Task<IReadOnlyList<AccountManagerChoice>> GetAsync(CancellationToken ct = default)
    {
        var heads = await context.Users.AsNoTracking().Where(u => context.Departments.Any(d => d.ResponsibleUserId == u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName }).ToListAsync(ct);
        return new[] { new AccountManagerChoice(null, "Geen") }.Concat(heads
            .Select(u => new AccountManagerChoice(u.Id, $"{u.FirstName} {u.LastName}".Trim()))
            .OrderBy(u => u.DisplayName, StringComparer.Ordinal).ThenBy(u => u.UserId, StringComparer.Ordinal)).ToArray();
    }

    public Task<bool> IsAllowedAsync(string? id, CancellationToken ct = default) => string.IsNullOrWhiteSpace(id)
        ? Task.FromResult(true) : context.Departments.AsNoTracking().AnyAsync(d => d.ResponsibleUserId == id, ct);
}
