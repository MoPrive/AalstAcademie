namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Expliciete uitkomsten waarmee een toekomstige controller gericht kan reageren.</summary>
public enum InstructorProfileOutcome
{
    // Het juiste profiel is opgeslagen en de transactie is gecommit.
    Created,
    // Hetzelfde profiel bestond al; zijn gegevens zijn niet overschreven.
    AlreadyExists,
    // De opgegeven Identity-gebruiker bestaat niet.
    UserNotFound,
    // Geblokkeerd, afgewezen of met een niet-ondersteunde beoordelingsstatus.
    IneligibleAccount,
    // Ontbrekende of strijdige gegevens volgens AccountProfileRules.
    InvalidProfile,
    // Een medewerker, null of onbekend accounttype kan geen lesgeverprofiel krijgen.
    WrongAccountType,
    // Tegengesteld profiel, unieke-indexconflict of rechtstreeks gemelde SQLite-lock.
    Conflict
}
