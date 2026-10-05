// Sprint 003: Legt de vier vaste opslagwaarden Requested, Confirmed, Refused en Cancelled vast
// Alleen de eerste twee zijn bezetting; alle vier zijn historie.
namespace AalstAcademie.Web.Models.Domain;

/// <summary>De vier gekozen aanvraagtoestanden; uitsluitend Requested en Confirmed bezetten plaatsen.</summary>
public enum RegistrationStatus
{
    Requested = 0,
    Confirmed = 1,
    Refused = 2,
    Cancelled = 3
}
