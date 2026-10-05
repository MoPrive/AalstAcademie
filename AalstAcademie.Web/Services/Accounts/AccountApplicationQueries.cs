using System.Data;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Accounts;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Leesmodellen voor beheerschermen; autorisatie blijft bij de beveiligde controller/policies.</summary>
public sealed class AccountApplicationQueries(ApplicationDbContext context)
{
    /// <summary>Begrenst zowel databasewerk als het aantal rijen dat één refresh naar de browser stuurt.</summary>
    public const int PageSize = 50;

    /// <summary>Een readtransactie laat teller, paginering en rijen dezelfde opgeslagen toestand zien.</summary>
    public async Task<AccountApplicationsSnapshot> GetSnapshotAsync(AccountApprovalStatus status = AccountApprovalStatus.Pending,
        int page = 1, CancellationToken cancellationToken = default)
    {
        // Onbekende enumwaarden krijgen dezelfde veilige startweergave als een ontbrekend filter.
        if (!Enum.IsDefined(status)) status = AccountApprovalStatus.Pending;
        // Iedere aanvraag gebruikt dezelfde scoped context. Een bestaande transactie is eigendom van de caller.
        await using var ownedTransaction = context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        // De badge telt alle wachtende aanvragen, onafhankelijk van het gekozen lijstfilter.
        var totalPending = await context.Users.AsNoTracking()
            .CountAsync(x => x.AccountApprovalStatus == AccountApprovalStatus.Pending, cancellationToken);
        var filtered = context.Users.AsNoTracking().Where(x => x.AccountApprovalStatus == status);
        var total = await filtered.CountAsync(cancellationToken);
        // Ook een lege lijst heeft pagina 1. Clamp voorkomt negatieve offsets en verdwenen laatste pagina's.
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, totalPages);
        // DateTime is sorteerbaar in SQLite. De ID maakt ook null/gelijke datums deterministisch.
        var rows = await filtered.OrderBy(x => x.AccountRequestedAtUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(x => new
            {
                x.Id, x.FirstName, x.LastName, x.Email, x.RequestedAccountType, x.AccountRequestedAtUtc,
                x.AccountApprovalStatus, x.ConcurrencyStamp,
                Department = x.Department == null ? null : x.Department.Name,
                Organization = context.ExternalInstructors.Where(p => p.ApplicationUserId == x.Id)
                    .Select(p => p.OrganizationName).FirstOrDefault()
            }).ToListAsync(cancellationToken);
        var result = rows.Select(x => new AccountApplicationRow(x.Id,
            DisplayName(x.FirstName, x.LastName, x.Organization, x.Email), x.RequestedAccountType?.ToString(),
            x.RequestedAccountType == RequestedAccountType.ExternalInstructor ? x.Organization : x.Department,
            x.AccountRequestedAtUtc, x.AccountApprovalStatus.ToString(), x.ConcurrencyStamp)).ToArray();
        // Een readcommit bewaart geen wijzigingen, maar markeert het consistente uitlezen als afgerond.
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return new(totalPending, status.ToString(), page, totalPages, result);
    }

    /// <summary>Leest een aanvraag zonder wachtwoordhash, securitystamp of identiteitsclaims in het viewmodel.</summary>
    public async Task<AccountApplicationDetailsViewModel?> GetDetailsAsync(string id, string actorUserId,
        CancellationToken cancellationToken = default)
    {
        var user = await context.Users.AsNoTracking().Include(x => x.Department).Include(x => x.Manager)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null) return null;
        var external = await context.ExternalInstructors.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ApplicationUserId == id, cancellationToken);
        var targetAdministrator = await context.UserRoles.AnyAsync(link => link.UserId == id &&
            context.Roles.Any(role => role.Id == link.RoleId && role.NormalizedName == RoleNames.Beheerder.ToUpper()),
            cancellationToken);
        var departments = await context.Departments.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync(cancellationToken);
        return new()
        {
            Id = user.Id, DisplayName = DisplayName(user.FirstName, user.LastName, external?.OrganizationName, user.Email),
            RequestedAccountType = user.RequestedAccountType, Status = user.AccountApprovalStatus,
            Email = user.Email, PhoneNumber = user.PhoneNumber, FirstName = user.FirstName, LastName = user.LastName,
            // De presentatie leest de actuele persoonlijke naam, ook als de manager geen head meer is.
            DepartmentName = user.Department?.Name, ManagerName = user.Manager is null ? null : $"{user.Manager.FirstName} {user.Manager.LastName}".Trim(),
            OrganizationName = external?.OrganizationName, VatNumber = external?.VatNumber,
            AccountRequestedAtUtc = user.AccountRequestedAtUtc, ReviewedAt = user.ReviewedAt, RefusalReason = user.RefusalReason,
            // Dit is alleen een schermkeuze. De schrijfservice herhaalt de controle met actuele DB-gegevens.
            CanReview = id != actorUserId && !targetAdministrator && user.AccountApprovalStatus != AccountApprovalStatus.Approved,
            Input = new() { ExpectedConcurrencyStamp = user.ConcurrencyStamp ?? string.Empty, DepartmentId = user.DepartmentId },
            Departments = departments
        };
    }

    /// <summary>Ook onvolledige bestaande accounts krijgen een begrijpelijke naam zonder fictieve profielgegevens.</summary>
    private static string DisplayName(string? firstName, string? lastName, string? organization, string? email)
    {
        if (!string.IsNullOrWhiteSpace(organization)) return organization;
        var personalName = $"{firstName} {lastName}".Trim();
        return string.IsNullOrWhiteSpace(personalName) ? email ?? "Onvolledige aanvraag" : personalName;
    }
}
