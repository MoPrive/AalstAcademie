using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>
/// Maakt gecontroleerd het lesgeverprofiel aan dat bij een opgeslagen account hoort.
/// De via dependency injection ontvangen context verzorgt de databasebewerkingen.
/// </summary>
public sealed class InstructorProfileService(ApplicationDbContext context)
{
    /// <summary>
    /// Leest het accounttype uit de database, valideert en schrijft maximaal één passend profiel.
    /// Een CancellationToken kan de databasebewerkingen annuleren; er worden geen rollen toegekend.
    /// Created is zelfstandig gecommit; binnen een bestaande transactie volgt commit door de caller.
    /// </summary>
    /// <remarks>
    /// In sprint 002 roept registratie of demo-bootstrap deze helper pas na de Identity-insert aan.
    /// De gedeelde DbContext laat de helper die nog niet gecommitte gebruiker lezen en bewaart
    /// account plus profiel atomisch. Een Created-resultaat binnen die batch is nog geen toegang.
    /// </remarks>
    public async Task<InstructorProfileOutcome> CreateAsync(string userId,
        string? organizationName = null, string? vatNumber = null, CancellationToken cancellationToken = default)
    {
        // Houd alleen het nieuw toegevoegde object bij om het na een opslagconflict
        // uit EF-tracking te verwijderen. Anders kan een volgende SaveChanges het opnieuw proberen.
        object? addedProfile = null;
        IDbContextTransaction? ownedTransaction = null;
        var completed = false;
        try
        {
            // De niet-uitgestelde SQLite-schrijftransactie houdt de controles van beide
            // tabellen en de insert bij elkaar. Alleen losse unieke indexen volstaan niet
            // om één profieltype over twee tabellen heen af te dwingen.
            // Registratie en demo-bootstrap bewaren account + profiel in hun eigen transactie.
            // Neem die over; alleen een zelfstandig aangeroepen helper opent een eigen transactie.
            if (context.Database.CurrentTransaction is null)
                ownedTransaction = await context.Database.BeginTransactionAsync(cancellationToken);
            // Vertrouw de opgeslagen gebruiker. AsNoTracking voorkomt wijzigingstracking
            // van deze gelezen kopie; het accounttype komt niet uit een formulierparameter.
            var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
            if (user is null) return InstructorProfileOutcome.UserNotFound;
            // Een profiel kan vóór de beoordeling worden voorbereid; dat verleent nog geen toegang.
            if (user.IsBlocked || user.AccountApprovalStatus is not (AccountApprovalStatus.Pending or AccountApprovalStatus.Approved))
                return InstructorProfileOutcome.IneligibleAccount;
            if (user.RequestedAccountType is not (RequestedAccountType.InternalInstructor or RequestedAccountType.ExternalInstructor))
                return InstructorProfileOutcome.WrongAccountType;

            // Controleer ook de andere tabel: een unieke FK beschermt alleen de eigen tabel.
            var internalProfile = await context.InternalInstructors.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ApplicationUserId == userId, cancellationToken);
            var externalProfile = await context.ExternalInstructors.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ApplicationUserId == userId, cancellationToken);
            var isInternal = user.RequestedAccountType == RequestedAccountType.InternalInstructor;
            if ((isInternal && externalProfile is not null) || (!isInternal && internalProfile is not null))
                return InstructorProfileOutcome.Conflict;

            // Herhaling valideert de bestaande externe gegevens en overschrijft die niet.
            var errors = AccountProfileRules.Validate(user,
                externalProfile?.OrganizationName ?? organizationName,
                externalProfile is not null ? externalProfile.VatNumber : vatNumber);
            if (errors.Count != 0) return InstructorProfileOutcome.InvalidProfile;
            // Idempotent gedrag: dezelfde geldige aanvraag creëert geen tweede profiel.
            if (internalProfile is not null || externalProfile is not null) return InstructorProfileOutcome.AlreadyExists;

            // De naam/afdeling van een intern profiel blijven uitsluitend op ApplicationUser.
            if (isInternal)
            {
                var profile = new InternalInstructor { ApplicationUserId = userId };
                context.InternalInstructors.Add(profile);
                addedProfile = profile;
            }
            else
            {
                // Bewaar organisatiegegevens op het externe profiel; spaties aan de randen
                // verdwijnen en een niet-ingevuld btw-nummer wordt als null opgeslagen.
                var profile = new ExternalInstructor
                {
                    ApplicationUserId = userId, OrganizationName = organizationName!.Trim(),
                    VatNumber = string.IsNullOrWhiteSpace(vatNumber) ? null : vatNumber.Trim()
                };
                context.ExternalInstructors.Add(profile);
                addedProfile = profile;
            }
            // SaveChanges verstuurt de insert; pas Commit maakt de transactie definitief.
            // Alleen de eigenaar bepaalt commit; de buitenste caller verwerkt zijn andere wijzigingen.
            await context.SaveChangesAsync(cancellationToken);
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
            completed = true;
            return InstructorProfileOutcome.Created;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            // SQLite 2067 is een unieke-indexovertreding, bijvoorbeeld door een directe
            // schrijver buiten deze service. Geef een herkenbaar conflict terug, geen succes.
            if (addedProfile is not null) context.Entry(addedProfile).State = EntityState.Detached;
            return InstructorProfileOutcome.Conflict;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            // BUSY/LOCKED betekent dat SQLite de bewerking niet kon uitvoeren door een lock.
            // Deze specifieke fouten worden vertaald; andere fouten blijven zichtbaar.
            if (addedProfile is not null) context.Entry(addedProfile).State = EntityState.Detached;
            return InstructorProfileOutcome.Conflict;
        }
        finally
        {
            // Bij deelname raakt de helper de levensduur van de buitenste transactie niet aan.
            // Een fout wordt teruggegeven zodat registratie/bootstrap als eigenaar alles terugdraait.
            if (ownedTransaction is not null)
            {
                try { if (!completed) await ownedTransaction.RollbackAsync(CancellationToken.None); }
                finally { await ownedTransaction.DisposeAsync(); }
            }
            // Ook een niet-vertaalde exception mag de eigen profielinsert niet achterlaten in EF.
            // Andere entiteiten horen bij de caller en worden hier niet losgekoppeld.
            if (!completed && addedProfile is not null) context.Entry(addedProfile).State = EntityState.Detached;
        }
    }
}
