// Sprint 003: Legt Scheduled=1 en Cancelled=2 vast
// De opslag ondersteunt annulering, maar sprint 003 voegt geen annuleringsroute toe.
namespace AalstAcademie.Web.Models.Domain;

/// <summary>De gekozen vaste opslagwaarden; annulering is een afzonderlijke toekomstige gebruikersflow.</summary>
public enum TrainingMomentStatus
{
    Scheduled = 1,
    Cancelled = 2
}
