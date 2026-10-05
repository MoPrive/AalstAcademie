// Sprint 003: Bewaart belangstelling voor een opleiding met user en UTC-datums
// ClosedAtUtc=null betekent actief; ook gesloten rijen blijven historie voor de voorwaardenlock.
using AalstAcademie.Web.Models.Identity;

namespace AalstAcademie.Web.Models.Domain;

/// <summary>Belangstelling voor de opleiding; een lege sluitingsdatum betekent actief, zonder beoordelingsstatus.</summary>
public class WaitlistEntry
{
    public int Id { get; set; }
    public int TrainingId { get; set; }
    public Training Training { get; set; } = null!;
    public string ApplicationUserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public string? Motivation { get; set; }
    // Beide datums zijn expliciet UTC. Gesloten historie blijft bestaan; geen moment- of bron-FK.
    public DateTime JoinedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
}
