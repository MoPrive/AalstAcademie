namespace AalstAcademie.Web.Models.Identity;

/// <summary>De drie publieke accountaanvragen; een beheerder is geen registratiekeuze.</summary>
// De expliciete getallen worden in SQLite bewaard. Hun betekenis moet bij latere
// uitbreidingen gelijk blijven, zodat opgeslagen accounts niet van type veranderen.
public enum RequestedAccountType
{
    // Medewerker met persoonlijke naam, afdeling en leidinggevende.
    Employee = 1,
    // Interne lesgever met persoonlijke naam en afdeling.
    InternalInstructor = 2,
    // Externe lesgever waarvan de organisatie de lesgeversnaam vormt.
    ExternalInstructor = 3
}
