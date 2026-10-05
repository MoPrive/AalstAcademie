// Sprint 003: Projecteert de geplande uitvoering, gedeelde labels, beheerflags, bezetting en zaalbeschikbaarheid
// Er worden geen deelnemernamen of motivaties uitgestuurd.
using AalstAcademie.Web.Models.Domain;

namespace AalstAcademie.Web.Models.Training;

/// <summary>Alleen beheergegevens van een uitvoering; uren bewaren de volledige opgeslagen tickprecisie.</summary>
public sealed record TrainingMomentRowReadModel(int Id, int TrainingId, string TrainingTitle,
    int LocationId, string LocationName, int LocationMaximumCapacity, DateOnly Date,
    TimeOnly StartTime, TimeOnly EndTime, int MaximumParticipants, TrainingMomentStatus Status, Guid Version);

/// <summary>Actiefeedback en noodzakelijke capaciteitsteller voor geautoriseerd beheer, zonder deelnemersgegevens.</summary>
public sealed record TrainingMomentDetailsReadModel(TrainingMomentRowReadModel Moment,
    bool CanEditPlanning, bool CanEditCapacity, bool HasRegistrations, int OccupiedCount)
{
    public bool CanCancel { get; init; }
}

/// <summary>Een beschikbaarheidsread toont zaal/interval/capaciteit; dit legt geen boeking of deelnemerslijst vast.</summary>
public sealed record TrainingAvailabilityRoomReadModel(int Id, string Name, string? Address,
    int MaximumCapacity, bool IsAvailable);

/// <summary>De gekozen planning reist afzonderlijk mee naar een eventuele POST en moet daar opnieuw worden getoetst.</summary>
public sealed record TrainingAvailabilityReadModel(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime,
    IReadOnlyList<TrainingAvailabilityRoomReadModel> Rooms);
