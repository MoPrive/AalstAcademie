using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Common;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>
/// Bewaart een openbare aanvraag en het passende profiel als één geheel.
/// Dezelfde scoped context wordt door Identity en de profielhelper gebruikt.
/// </summary>
public sealed class AccountRegistrationService(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
    InstructorProfileService profileService, TimeProvider timeProvider, IAccountApplicationNotifier notifier,
    ILogger<AccountRegistrationService> logger, AccountManagerChoices managerChoices)
{
    /// <summary>
    /// Accepteert uitsluitend aanvraagvelden; goedkeuring, rollen en demo-identiteit worden nooit ingevoerd.
    /// Succes betekent dat de transactie klaar is; aanmelding mag pas daarna plaatsvinden.
    /// </summary>
    /// <remarks>
    /// Deze service schrijft de aanvraag maar maakt zelf geen cookie. De Razor Page bepaalt
    /// de vervolgnavigatie nadat user, eventueel profiel en aanvraagdatum samen zijn opgeslagen.
    /// </remarks>
    public async Task<AccountOperationResult> RegisterAsync(AccountRegistrationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Registratie is zelf de eigenaar. Een buitenste transactie zou het succes- en
        // notificatiemoment onduidelijk maken en is daarom een fout in servicecompositie.
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Registratie kan niet binnen een bestaande transactie worden uitgevoerd.");

        // Wachtwoorden blijven exact zoals ingevoerd; alleen contact- en profieltekst wordt getrimd.
        var user = new ApplicationUser
        {
            Email = Text(command.Email), UserName = Text(command.Email), PhoneNumber = Text(command.PhoneNumber),
            RequestedAccountType = command.RequestedAccountType, FirstName = Text(command.FirstName),
            LastName = Text(command.LastName), DepartmentId = command.DepartmentId, ManagerUserId = Text(command.ManagerUserId),
            AccountApprovalStatus = AccountApprovalStatus.Pending, IsBlocked = false, EmailConfirmed = false,
            AccountRequestedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        var organization = Text(command.OrganizationName);
        var vat = Text(command.VatNumber);
        var errors = new Dictionary<string, string>(AccountProfileRules.Validate(user, organization, vat), StringComparer.Ordinal);
        ValidateCommon(command, user, errors);
        // Sprint 004: beide interne types mogen Geen of een momenteel gekoppelde head kiezen.
        // Veldfouten stoppen vóór de eerste insert; een browser kan deze checks niet omzeilen met een eigen POST.
        if (errors.Count != 0) return AccountOperationResult.Invalid(errors);

        // Alleen nieuw getrackte operatie-objecten worden bij mislukking losgekoppeld.
        // Bestaande referentiegegevens en eventuele andere callertracking blijven intact.
        var previouslyTracked = context.ChangeTracker.Entries().Select(x => x.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        IDbContextTransaction? transaction = null;
        if (context.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("Registratie vereist de geconfigureerde SQLite-provider.");
        var wasOpen = connection.State == ConnectionState.Open;
        var priorTimeout = connection.DefaultTimeout;
        var priorCommandTimeout = context.Database.GetCommandTimeout();
        SqliteTransaction? nativeTransaction = null;
        var committed = false;
        try
        {
            // Reserveer de schrijver voordat de FK-keuze wordt gelezen. De profielhelper
            // gebruikt deze EF-enlistment; er wordt geen tweede transactie gestart.
            connection.DefaultTimeout = 5; context.Database.SetCommandTimeout(5);
            if (!wasOpen) await context.Database.OpenConnectionAsync(cancellationToken);
            nativeTransaction = connection.BeginTransaction(deferred: false);
            transaction = await context.Database.UseTransactionAsync(nativeTransaction, cancellationToken)
                ?? throw new InvalidOperationException("De registratie kon niet aan de schrijftransactie deelnemen.");
            if (user.DepartmentId is not null && !await context.Departments.AsNoTracking()
                    .AnyAsync(x => x.Id == user.DepartmentId, cancellationToken))
                return AccountOperationResult.Invalid(new Dictionary<string, string>
                    { [nameof(command.DepartmentId)] = "Kies een bestaande afdeling." });
            if (!await managerChoices.IsAllowedAsync(user.ManagerUserId, cancellationToken))
                return AccountOperationResult.Invalid(new Dictionary<string, string>
                    { [nameof(command.ManagerUserId)] = "Kies een huidige afdelingsverantwoordelijke of Geen." });
            // Identity normaliseert e-mail en gebruikersnaam. De unieke gebruikersnaamindex
            // blijft ook bij een concurrerende aanvraag de definitieve databasebescherming.
            if (await userManager.FindByEmailAsync(user.Email!) is not null)
                return AccountOperationResult.Invalid(new Dictionary<string, string>
                    { [nameof(command.Email)] = "Voor dit e-mailadres bestaat al een account." });
            cancellationToken.ThrowIfCancellationRequested();
            var creation = await userManager.CreateAsync(user, command.Password!);
            if (!creation.Succeeded) return IdentityFailure(creation);
            cancellationToken.ThrowIfCancellationRequested();

            // Medewerkers hebben geen lesgeverprofiel; beide lesgevertype-tabellen koppelen naar dezelfde user-ID.
            if (user.RequestedAccountType is RequestedAccountType.InternalInstructor or RequestedAccountType.ExternalInstructor)
            {
                // CreateAsync leest de inmiddels opgeslagen gebruiker en neemt deze transactie over.
                var profile = await profileService.CreateAsync(user.Id, organization, vat, cancellationToken);
                if (profile != InstructorProfileOutcome.Created)
                    return new AccountOperationResult(AccountOperationStatus.Conflict);
            }
            // Er is bewust geen AddToRole: de beheerder beslist later over alle toegang.
            await transaction.CommitAsync(cancellationToken);
            committed = true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // Constraints of een gelijktijdige insert geven een gecontroleerd conflict.
            // SQL- en providerfoutteksten zijn geen veilige formuliermeldingen.
            return new AccountOperationResult(AccountOperationStatus.Conflict);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return new AccountOperationResult(AccountOperationStatus.Conflict);
        }
        catch (DbUpdateException) { return new(AccountOperationStatus.StorageUnavailable); }
        catch (DbException) { return new(AccountOperationStatus.StorageUnavailable); }
        finally
        {
            try
            {
                try
                {
                    if (transaction is not null)
                    {
                        try { if (!committed) await transaction.RollbackAsync(CancellationToken.None); }
                        finally { await transaction.DisposeAsync(); }
                    }
                }
                finally { if (nativeTransaction is not null) await nativeTransaction.DisposeAsync(); }
            }
            finally
            {
                context.Database.SetCommandTimeout(priorCommandTimeout);
                connection.DefaultTimeout = priorTimeout;
                if (!wasOpen && connection.State != ConnectionState.Closed) await context.Database.CloseConnectionAsync();
                if (!committed)
                    foreach (var entry in context.ChangeTracker.Entries().Where(x => !previouslyTracked.Contains(x.Entity)).ToList())
                        entry.State = EntityState.Detached;
            }
        }

        // Dispose verwijdert CurrentTransaction voordat de melding plaatsvindt. De onafhankelijke,
        // begrensde best-effort dispatch kan opgeslagen succes niet meer ongedaan maken.
        await notifier.NotifyCommittedAsync(logger);
        return new AccountOperationResult(AccountOperationStatus.Success, user.Id);
    }

    /// <summary>Gemeenschappelijke servervalidatie staat los van browservelden en van profielgoedkeuring.</summary>
    private static void ValidateCommon(AccountRegistrationCommand command, ApplicationUser user, Dictionary<string, string> errors)
    {
        if (user.Email is null || user.Email.Length > 256 || !new EmailAddressAttribute().IsValid(user.Email))
            errors[nameof(command.Email)] = "Vul een geldig e-mailadres in van maximaal 256 tekens.";
        if (user.PhoneNumber is null || user.PhoneNumber.Length > 50 || !new PhoneAttribute().IsValid(user.PhoneNumber))
            errors[nameof(command.PhoneNumber)] = "Vul een geldig telefoonnummer in van maximaal 50 tekens.";
        if (string.IsNullOrEmpty(command.Password)) errors[nameof(command.Password)] = "Vul een wachtwoord in.";
        if (string.IsNullOrEmpty(command.ConfirmPassword) || !string.Equals(command.Password, command.ConfirmPassword, StringComparison.Ordinal))
            errors[nameof(command.ConfirmPassword)] = "De wachtwoorden moeten overeenkomen.";
    }

    /// <summary>Identity blijft verantwoordelijk voor wachtwoord- en unieke gebruikersnaamregels.</summary>
    private static AccountOperationResult IdentityFailure(IdentityResult result)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var error in result.Errors)
        {
            var field = error.Code.StartsWith("Password", StringComparison.Ordinal) ? nameof(AccountRegistrationCommand.Password)
                : error.Code.Contains("Email", StringComparison.Ordinal) || error.Code.Contains("UserName", StringComparison.Ordinal)
                    ? nameof(AccountRegistrationCommand.Email) : string.Empty;
            // Meerdere wachtwoordregels mogen op één veld zichtbaar blijven; geen exceptiontekst.
            errors[field] = errors.TryGetValue(field, out var prior) ? prior + " " + error.Description : error.Description;
        }
        return AccountOperationResult.Invalid(errors);
    }

    /// <summary>Null en uitsluitend spaties vormen dezelfde afwezige profielwaarde.</summary>
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
