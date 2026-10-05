// Sprint 003: Schrijft na de gecontroleerde reset de zes fictieve accounts met passende profielen, rollen, goedkeuring en nieuwe stamps
// De batch publiceert zelf nog geen HTTP-loginhint.
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Data;

/// <summary>
/// Bouwt na de geautoriseerde demoreset één volledige fictieve accountbatch op.
/// Bestaande reserveringen zijn een fout; de service adopteert of repareert geen accounts.
/// </summary>
public sealed class DemoDataSeeder(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
    InstructorProfileService profileService, DemoMode demoMode, TimeProvider timeProvider, DemoCredentials credentials)
{
    /// <summary>Controleert reserveringen vóór generatie en commit alle zes accounts als één batch.</summary>
    public async Task<DemoSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!demoMode.IsEnabled) return DemoSeedResult.Empty;
        // Geen normalisaties/reparaties buiten demo, ook niet bij rechtstreekse service-aanroep.
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Demo-initialisatie vereist een eigen batchtransactie.");
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var seed in DemoAccountCatalog.All)
            {
                var normalizedEmail = userManager.NormalizeEmail(seed.Email)!;
                // Controleer ook ruwe legacy-email en genormaliseerde login: zij mogen een reservering
                // niet onzichtbaar bezetten doordat een oude schrijver een normalisatieveld leeg liet.
                var matches = await context.Users.AsNoTracking().Where(x => x.Id == seed.Id || x.DemoSeedKey == seed.Key ||
                    x.NormalizedEmail == normalizedEmail || x.NormalizedUserName == normalizedEmail ||
                    (x.Email != null && x.Email.ToUpper() == normalizedEmail)).ToListAsync(cancellationToken);
                // Ook een volledige eerdere batch is geen toestemming tot herstel binnen dezelfde sessie.
                if (matches.Count != 0)
                    throw new InvalidOperationException("Demo-initialisatie is afgebroken: een gereserveerde demo-identiteit, marker of e-mail is bezet.");
            }

            // De gewone initializer moet referentiedata al hebben klaargezet; deze seeder repareert ze niet.
            var department = await context.Departments.AsNoTracking().SingleOrDefaultAsync(x => x.Name == "ICT", cancellationToken);
            if (department is null)
                throw new InvalidOperationException("De referentieafdelingen moeten vóór demo-initialisatie aanwezig zijn.");
            foreach (var role in new[] { RoleNames.Beheerder, RoleNames.Medewerker, RoleNames.Lesgever })
            {
                var normalizedRole = userManager.NormalizeName(role);
                if (!await context.Roles.AnyAsync(x => x.NormalizedName == normalizedRole, cancellationToken))
                    throw new InvalidOperationException("De referentierollen moeten vóór demo-initialisatie aanwezig zijn.");
            }

            var now = timeProvider.GetUtcNow();
            // Alle accounts krijgen dezelfde alleen-in-geheugenwaarde; Identity bewaart normale individuele hashes/stamps.
            var password = credentials.PrepareAccountBatch();
            // De vaste lijst zet de demo-beheerder vóór zijn reeds goedgekeurde deelnemers,
            // zodat hun ReviewedById tijdens dezelfde batch naar een aanwezige account verwijst.
            foreach (var seed in DemoAccountCatalog.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isInternalApplication = seed.Type is RequestedAccountType.Employee or RequestedAccountType.InternalInstructor;
                var approvedApplication = seed.Type is not null && seed.Approval == AccountApprovalStatus.Approved;
                var user = new ApplicationUser
                {
                    Id = seed.Id, UserName = seed.Email, Email = seed.Email, DemoSeedKey = seed.Key,
                    RequestedAccountType = seed.Type, AccountApprovalStatus = seed.Approval,
                    FirstName = seed.FirstName, LastName = seed.LastName, ManagerUserId = null,
                    DepartmentId = isInternalApplication ? department.Id : null, PhoneNumber = "0490000000",
                    AccountRequestedAtUtc = seed.Type is null ? null : now.UtcDateTime,
                    ReviewedById = approvedApplication ? DemoAccountCatalog.AdministratorId : null,
                    ReviewedAt = approvedApplication ? now : null,
                    // Administratieve goedkeuring blijft onafhankelijk van e-mailverificatie.
                    EmailConfirmed = false, IsBlocked = false
                };
                EnsureIdentitySucceeded(await userManager.CreateAsync(user, password));
                if (seed.Type is RequestedAccountType.InternalInstructor or RequestedAccountType.ExternalInstructor)
                {
                    // De helper neemt deel aan deze transactie en mag haar niet zelfstandig committen.
                    var profile = await profileService.CreateAsync(user.Id, seed.OrganizationName, cancellationToken: cancellationToken);
                    if (profile != InstructorProfileOutcome.Created)
                        throw new InvalidOperationException("Het bijbehorende demolesgeverprofiel kon niet worden aangemaakt.");
                }
                var roles = seed.Type is null ? new[] { RoleNames.Beheerder } :
                    seed.Approval == AccountApprovalStatus.Approved ? RoleNames.ForApprovedAccountType(seed.Type).ToArray() : [];
                if (roles.Length != 0) EnsureIdentitySucceeded(await userManager.AddToRolesAsync(user, roles));
            }

            // Sprint 004: pas na alle accountinserts bestaan de doelen. Geen fictieve Sam-account
            // of automatische afdelingsovererving: Jules is ICT-head, Noor kiest Jules en Jules zichzelf.
            await context.Departments.Where(x => x.Id == department.Id).ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.ResponsibleUserId, DemoAccountCatalog.InternalInstructorId), cancellationToken);
            await context.Users.Where(x => x.Id == "demo-002-employee" || x.Id == DemoAccountCatalog.InternalInstructorId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ManagerUserId, DemoAccountCatalog.InternalInstructorId), cancellationToken);
            // Accountcommit is nog geen webpublicatie; de bootstrapcaller voltooit haar overige stappen eerst.
            await transaction.CommitAsync(cancellationToken);
            credentials.CompleteAccountBatch();
            return new DemoSeedResult(true, DemoAccountCatalog.All.Count);
        }
        catch (Exception exception)
        {
            // Annulering mag opruiming niet verhinderen. Een nieuwe poging krijgt geen oude tracked inserts mee.
            await transaction.RollbackAsync(CancellationToken.None);
            context.ChangeTracker.Clear();
            if (exception is OperationCanceledException or InvalidOperationException) throw;
            throw new InvalidOperationException("Demo-initialisatie is volledig teruggedraaid na een opslagfout.", exception);
        }
    }

    /// <summary>Maakt een niet-geslaagd IdentityResult tot een batchfout die de volledige demo-opslag terugdraait.</summary>
    private static void EnsureIdentitySucceeded(IdentityResult result)
    {
        // Identity kan zonder exception falen; er mag dan geen gedeeltelijke batch blijven bestaan.
        if (!result.Succeeded) throw new InvalidOperationException("Demo-initialisatie is afgebroken omdat Identity een account- of rolbewerking weigerde.");
    }

}
