namespace AalstAcademie.Web.Models.Identity;

/// <summary>Administratieve beoordelingsstatus, onafhankelijk van e-mailbevestiging en rollen.</summary>
public enum AccountApprovalStatus
{
    // Veilige standaard voor nieuwe én naar het nieuwe schema gemigreerde accounts.
    Pending = 0,
    // Beheerder heeft het account goedgekeurd; deze waarde kent op zichzelf geen rollen toe.
    Approved = 1,
    // Afgewezen accounts mogen geen lesgeverprofiel laten aanmaken.
    Refused = 2
}
