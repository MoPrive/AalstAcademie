// Sprint 003: Bewaart de herbruikbare definitie met metadata, vaste ApplicationUser-eigenaar, doelgroep en voorwaarden
// Datum, uren, zaal en maximum horen bij TrainingMoment.
using AalstAcademie.Web.Models.Identity;

namespace AalstAcademie.Web.Models.Domain;

/// <summary>
/// Herbruikbare opleidingsdefinitie met één vaste account-eigenaar. De afzonderlijke
/// momenten bewaren planning en zaal; historie verwijst naar deze gedeelde metadata.
/// </summary>
public class Training
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    // Gewone tekst met eventuele regelafbrekingen; de latere view encodeert deze inhoud.
    public string? Description { get; set; }
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;

    // Totale externe EUR-prijs, nooit een bedrag per deelnemer. Intern is deze waarde altijd null.
    public decimal? ExternalTotalPriceEuros { get; set; }

    // Het account blijft dezelfde eigenaar, ook bij later rolverlies of blokkering.
    // Actuele beheerrechten en prijs/profielcompatibiliteit controleert de service afzonderlijk.
    public string InstructorUserId { get; set; } = string.Empty;
    public ApplicationUser Instructor { get; set; } = null!;

    public TrainingAudienceScope AudienceScope { get; set; } = TrainingAudienceScope.Unassigned;
    public bool RequiresMotivation { get; set; }
    // Een eigen applicatieversie voor CAS; deze verandert geen Identity-securitystamp of cookie.
    public Guid Version { get; set; } = Guid.NewGuid();
    public ICollection<TrainingDepartment> TrainingDepartments { get; set; } = new List<TrainingDepartment>();
    public ICollection<TrainingMoment> Moments { get; set; } = new List<TrainingMoment>();
    public ICollection<WaitlistEntry> WaitlistEntries { get; set; } = new List<WaitlistEntry>();
}
