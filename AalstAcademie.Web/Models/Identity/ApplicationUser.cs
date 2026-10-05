using AalstAcademie.Web.Models.Domain;
using Microsoft.AspNetCore.Identity;

namespace AalstAcademie.Web.Models.Identity;

/// <summary>
/// Breidt de standaard Identity-gebruiker uit met profielgegevens en de aparte
/// beoordeling door een beheerder. E-mail, telefoon en wachtwoordhash blijven geërfd.
/// </summary>
public class ApplicationUser : IdentityUser
{
    // De aangevraagde hoedanigheid is geen verleende rol. Null laat bestaande,
    // onvolledige accounts toe zonder ze een accounttype of rechten toe te kennen.
    public RequestedAccountType? RequestedAccountType { get; set; }
    // Administratieve goedkeuring staat los van Identity.EmailConfirmed.
    public AccountApprovalStatus AccountApprovalStatus { get; set; } = AccountApprovalStatus.Pending;
    // Nullable voor de migratie van oude accounts en voor externe organisaties.
    // AccountProfileRules controleert later of een volledig profiel deze velden nodig heeft.
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    // Eén centrale afdelingskoppeling: interne lesgevers bewaren geen tweede kopie.
    // De FK identificeert de afdeling; beheerderverificatie volgt in de accountworkflow.
    public int? DepartmentId { get; set; }
    // Navigatie naar het gerelateerde object; DepartmentId is de opgeslagen sleutel.
    public Department? Department { get; set; }
    // Sprint 004: de optionele leidinggevende is een bestaande accountreferentie.
    // Deze koppeling verleent geen rollen en verandert de geverifieerde werkafdeling niet.
    public string? ManagerUserId { get; set; }
    public ApplicationUser? Manager { get; set; }
    // Legt vast wie beoordeelde en wanneer. De reviewservice schrijft dit samen met
    // status, rollen en securitystamp in één transactie, zodat het auditspoor overeenkomt.
    public string? ReviewedById { get; set; }
    public ApplicationUser? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    // UTC DateTime is ook door SQLite sorteerbaar; null bewaart oude aanvragen zonder fictieve datum.
    // Heropenen behoudt deze oorspronkelijke datum; ReviewedAt beschrijft de latere beslissing.
    public DateTime? AccountRequestedAtUtc { get; set; }
    // De aanvrager kan zijn eigen weigeringreden lezen. Goedkeuren/heropenen wist deze reden;
    // het veld verleent geen rollen en bevestigt geen e-mail.
    public string? RefusalReason { get; set; }
    // Alleen bootstrap schrijft deze marker om gereserveerde demoaccounts veilig te herkennen.
    public string? DemoSeedKey { get; set; }
    // Aparte blokkadevlag: de profielservice, aanmelding en actuele toegangspolicies
    // wijzen een geblokkeerd account af, ook wanneer de cookie nog oude rollen bevat.
    public bool IsBlocked { get; set; }
}
