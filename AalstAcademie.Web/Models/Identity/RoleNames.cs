namespace AalstAcademie.Web.Models.Identity;

/// <summary>Centrale rolnamen en de gewenste rolcombinatie voor een goedgekeurd accounttype.</summary>
public static class RoleNames
{
    // Eén schrijfwijze voorkomt afwijkende namen tussen initialisatie en latere autorisatie.
    public const string Medewerker = "Medewerker";
    public const string Lesgever = "Lesgever";
    public const string Beheerder = "Beheerder";

    /// <summary>
    /// Geeft de gewenste rollen terug zonder een gebruiker te wijzigen.
    /// Werkelijke goedkeuring en UserManager-roltoekenning horen bij de latere workflow.
    /// </summary>
    public static IReadOnlyList<string> ForApprovedAccountType(RequestedAccountType? type) => type switch
    {
        RequestedAccountType.Employee => [Medewerker],
        // Een interne lesgever behoudt ook de toekomstige medewerkersrechten.
        RequestedAccountType.InternalInstructor => [Medewerker, Lesgever],
        RequestedAccountType.ExternalInstructor => [Lesgever],
        // Null/onbekend verleent niets; Beheerder komt nooit uit deze publieke mapping.
        _ => []
    };
}
