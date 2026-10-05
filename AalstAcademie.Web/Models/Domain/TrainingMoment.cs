// Sprint 003: Bewaart één uitvoering op één Belgische dag met tijden, zaal, maximum, Scheduled/Cancelled-status en eigen versie
// De eigenaar en voorwaarden worden via de definitie gelezen.
namespace AalstAcademie.Web.Models.Domain;

/// <summary>Eén uitvoering op één Belgische kalenderdag, met eigen zaal, maximum en concurrencyversie.</summary>
public class TrainingMoment
{
    public int Id { get; set; }
    public int TrainingId { get; set; }
    public Training Training { get; set; } = null!;
    // De converters bewaren datumtekst en exacte integer-ticks; geen host-local tijd of afronding.
    public DateOnly Date { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public int MaximumParticipants { get; set; }
    public TrainingMomentStatus Status { get; set; } = TrainingMomentStatus.Scheduled;
    public Guid Version { get; set; } = Guid.NewGuid();
    // Ook geweigerde/geannuleerde pogingen bewaren de oorspronkelijke planningreferentie.
    public ICollection<Registration> Registrations { get; set; } = new List<Registration>();
}
