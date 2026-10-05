// Sprint 003: Bewaart één echte registratiepoging voor een moment met user, status, motivatie, UTC-aanmaaktijd, versie en reden
// Sprint 004 maakt Requested-aanvragen; beoordeling volgt uitsluitend in sprint 005.
using AalstAcademie.Web.Models.Identity;

namespace AalstAcademie.Web.Models.Domain;

/// <summary>Compacte bewaarde aanvraagpoging; sprint 004 schrijft Requested, sprint 005 verzorgt de beoordeling.</summary>
public class Registration
{
    public int Id { get; set; }
    public int TrainingMomentId { get; set; }
    public TrainingMoment TrainingMoment { get; set; } = null!;
    public string ApplicationUserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public RegistrationStatus Status { get; set; } = RegistrationStatus.Requested;
    public string? Motivation { get; set; }
    // Servermatige UTC-aanmaaktijd van deze poging; geen lokale klok of extra beslis-/bronvelden.
    public DateTime RequestedAtUtc { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public string? DecisionReason { get; set; }
}
