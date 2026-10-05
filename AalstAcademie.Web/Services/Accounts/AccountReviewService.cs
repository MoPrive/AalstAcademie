using System.ComponentModel.DataAnnotations;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Beoordeelt één aanvraag met actuele beheerrechten, versiecontrole en atomische roltoekenning.</summary>
public sealed class AccountReviewService(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
    TimeProvider timeProvider, IAccountApplicationNotifier notifier, ILogger<AccountReviewService> logger)
{
    /// <summary>actorUserId wordt door de controller uit de aangemelde serveridentiteit gehaald.</summary>
    /// <remarks>
    /// De beslissing is één schrijftransactie: status, auditgegevens, rollen en sessiestamp
    /// slagen allemaal of worden allemaal teruggedraaid. Pas daarna volgt de schermmelding.
    /// </remarks>
    public async Task<AccountOperationResult> ReviewAsync(string actorUserId, AccountReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        // Deze use case beheert haar eigen commitgrens; een helper mag daar wel aan deelnemen.
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Beoordeling vereist een eigen transactie.");
        AccountOperationResult result;
        try
        {
            // SQLite reserveert de schrijver vóór de huidige rechten/versie worden gelezen.
            // Een tweede beslissing leest na de eerste commit een andere versie of krijgt een lockconflict.
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                result = await ApplyAsync(actorUserId, command, cancellationToken);
                if (result.Succeeded) await transaction.CommitAsync(cancellationToken);
                else
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    context.ChangeTracker.Clear();
                }
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                context.ChangeTracker.Clear();
                throw;
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            result = new(AccountOperationStatus.Conflict);
        }
        catch (DbUpdateException)
        {
            // Toon geen SQL/schema/foutdetails; alle tussenliggende grants zijn teruggedraaid.
            result = new(AccountOperationStatus.Conflict);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6 or 19)
        {
            context.ChangeTracker.Clear();
            result = new(AccountOperationStatus.Conflict);
        }
        // Eerst dispose, daarna melden. Requestannulering of transportfalen vervalst geen opgeslagen succes.
        if (result.Succeeded) await notifier.NotifyCommittedAsync(logger);
        return result;
    }

    /// <summary>Controleert eerst actor, doel en gelezen versie; pas daarna ontstaan wijzigingen in de eigen transactie.</summary>
    private async Task<AccountOperationResult> ApplyAsync(string actorUserId, AccountReviewCommand command,
        CancellationToken cancellationToken)
    {
        // AsNoTracking voorkomt dat een oude, reeds geladen actor actueel lijkt door EF's identity map.
        var actor = await context.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actorUserId, cancellationToken);
        if (actor is null || actor.IsBlocked || actor.AccountApprovalStatus != AccountApprovalStatus.Approved ||
            !await HasAdministratorRoleAsync(actor.Id, cancellationToken))
            return new(AccountOperationStatus.Forbidden);
        var target = await context.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.TargetUserId, cancellationToken);
        if (target is null) return new(AccountOperationStatus.NotFound);
        if (target.Id == actor.Id || await HasAdministratorRoleAsync(target.Id, cancellationToken))
            return new(AccountOperationStatus.Forbidden);
        // Een lege of oude versie mag nooit als "de laatste versie gebruiken" worden geïnterpreteerd.
        if (string.IsNullOrWhiteSpace(command.ExpectedConcurrencyStamp) ||
            !string.Equals(target.ConcurrencyStamp, command.ExpectedConcurrencyStamp, StringComparison.Ordinal))
            return new(AccountOperationStatus.Conflict);
        if (!Enum.IsDefined(command.Action))
            return Invalid("Action", "Kies een geldige beoordeling.");
        // Goedkeuren/weigeren kan alleen vanuit Pending; heropenen alleen vanuit Refused.
        // Zo kan een tweede beheerder een reeds afgeronde beslissing niet opnieuw toepassen.
        var expectedState = command.Action == AccountReviewAction.Reopen
            ? AccountApprovalStatus.Refused : AccountApprovalStatus.Pending;
        if (target.AccountApprovalStatus != expectedState) return new(AccountOperationStatus.Conflict);

        if (command.Action == AccountReviewAction.Approve)
        {
            var errors = await ValidateApprovalAsync(target, command.DepartmentId, cancellationToken);
            if (errors.Count != 0) return AccountOperationResult.Invalid(errors);
        }
        if (command.Action == AccountReviewAction.Refuse &&
            (string.IsNullOrWhiteSpace(command.RefusalReason) || command.RefusalReason.Trim().Length > 1000))
            return Invalid("RefusalReason", "Geef een reden van maximaal 1000 tekens.");

        // Gebruik de vers opgehaalde kopie; eerdere objecten uit dezelfde scope verlenen geen stale-update recht.
        var oldTrackedTarget = context.Users.Local.FirstOrDefault(x => x.Id == target.Id);
        if (oldTrackedTarget is not null) context.Entry(oldTrackedTarget).State = EntityState.Detached;
        context.Users.Attach(target);
        target.AccountApprovalStatus = command.Action switch
        {
            AccountReviewAction.Approve => AccountApprovalStatus.Approved,
            AccountReviewAction.Refuse => AccountApprovalStatus.Refused,
            _ => AccountApprovalStatus.Pending
        };
        target.RefusalReason = command.Action == AccountReviewAction.Refuse ? command.RefusalReason!.Trim() : null;
        target.ReviewedById = actor.Id;
        target.ReviewedAt = timeProvider.GetUtcNow();
        // AccountRequestedAtUtc blijft ongemoeid: heropenen is een nieuwe beoordeling van de oorspronkelijke aanvraag.
        // UserManager.UpdateAsync bewaart deze nieuwe stamp met dezelfde metadata/rollen in dezelfde transactie.
        // De latere cookievalidator wijst sessies met de oude stamp af.
        target.SecurityStamp = Guid.NewGuid().ToString();

        // SecurityStamp trekt oude sessies in; Identity's ConcurrencyStamp beschermt gelijktijdige accountupdates.
        // Dit zijn verschillende controles en geen van beide is een roltoekenning.
        var currentRoles = await userManager.GetRolesAsync(target);
        var participantRoles = currentRoles.Where(x => x is RoleNames.Medewerker or RoleNames.Lesgever).ToArray();
        if (participantRoles.Length != 0)
        {
            var remove = await userManager.RemoveFromRolesAsync(target, participantRoles);
            if (!remove.Succeeded) return IdentityFailure(remove);
        }
        if (command.Action == AccountReviewAction.Approve)
        {
            // Employee krijgt Medewerker; InternalInstructor Medewerker én Lesgever; ExternalInstructor alleen Lesgever.
            var add = await userManager.AddToRolesAsync(target, RoleNames.ForApprovedAccountType(target.RequestedAccountType));
            if (!add.Succeeded) return IdentityFailure(add);
        }
        // Controleer ook het laatste Identity-resultaat; een afwijzing vereist rollback van eerdere grants.
        var update = await userManager.UpdateAsync(target);
        return update.Succeeded ? new(AccountOperationStatus.Success, target.Id) : IdentityFailure(update);
    }

    /// <summary>Toetst de opgeslagen profielvorm en contactvelden; goedkeuring repareert geen ontbrekend lesgeverprofiel.</summary>
    private async Task<Dictionary<string, string>> ValidateApprovalAsync(ApplicationUser target, int? correctedDepartment,
        CancellationToken cancellationToken)
    {
        var internalProfile = await context.InternalInstructors.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ApplicationUserId == target.Id, cancellationToken);
        var externalProfile = await context.ExternalInstructors.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ApplicationUserId == target.Id, cancellationToken);
        if (target.RequestedAccountType is RequestedAccountType.Employee or RequestedAccountType.InternalInstructor)
            target.DepartmentId = correctedDepartment ?? target.DepartmentId;
        var errors = new Dictionary<string, string>(AccountProfileRules.Validate(target,
            externalProfile?.OrganizationName, externalProfile?.VatNumber), StringComparer.Ordinal);
        if (target.IsBlocked) errors["Account"] = "Een geblokkeerd account kan niet worden goedgekeurd.";
        if (string.IsNullOrWhiteSpace(target.Email) || !new EmailAddressAttribute().IsValid(target.Email))
            errors["Email"] = "Een geldig e-mailadres is vereist.";
        if (string.IsNullOrWhiteSpace(target.PhoneNumber) || !new PhoneAttribute().IsValid(target.PhoneNumber))
            errors["PhoneNumber"] = "Een geldig telefoonnummer is vereist.";
        if (target.RequestedAccountType == RequestedAccountType.ExternalInstructor && correctedDepartment is not null)
            errors["DepartmentId"] = "Een externe lesgever heeft geen interne afdeling.";
        if (target.RequestedAccountType is RequestedAccountType.Employee or RequestedAccountType.InternalInstructor &&
            !await context.Departments.AnyAsync(x => x.Id == target.DepartmentId, cancellationToken))
            errors["DepartmentId"] = "Kies een bestaande afdeling.";
        // Geen auto-aanmaak of conversie van een onvolledige legacy-aanvraag.
        var matchingProfile = target.RequestedAccountType switch
        {
            RequestedAccountType.Employee => internalProfile is null && externalProfile is null,
            RequestedAccountType.InternalInstructor => internalProfile is not null && externalProfile is null,
            RequestedAccountType.ExternalInstructor => externalProfile is not null && internalProfile is null,
            _ => false
        };
        if (!matchingProfile) errors["Profile"] = "Het opgeslagen profiel past niet volledig bij het aangevraagde type.";
        return errors;
    }

    /// <summary>Zoekt de actuele Identity-rolkoppeling op; een rolclaim uit een oude cookie is onvoldoende.</summary>
    private Task<bool> HasAdministratorRoleAsync(string userId, CancellationToken cancellationToken)
    {
        var normalizedRole = userManager.NormalizeName(RoleNames.Beheerder);
        return context.UserRoles.AnyAsync(link => link.UserId == userId &&
            context.Roles.Any(role => role.Id == link.RoleId && role.NormalizedName == normalizedRole), cancellationToken);
    }

    /// <summary>Vertaalt een bekende businessregel naar een veilig veldbericht voor het beoordelingsformulier.</summary>
    private static AccountOperationResult Invalid(string field, string message) =>
        AccountOperationResult.Invalid(new Dictionary<string, string> { [field] = message });

    /// <summary>Behoudt concurrency als herkenbaar conflict en verbergt overige Identity-opslagdetails.</summary>
    private static AccountOperationResult IdentityFailure(IdentityResult result) =>
        result.Errors.Any(x => x.Code == "ConcurrencyFailure") ? new(AccountOperationStatus.Conflict) :
            Invalid("", "De beoordeling kon niet worden opgeslagen. Er zijn geen wijzigingen doorgevoerd.");
}
