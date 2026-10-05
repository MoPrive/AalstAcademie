// Sprint 003: Combineert oorspronkelijke momentinvoer met scoped details en beschikbare zalen
// De oorspronkelijke versie en gekozen zaal blijven bij conflict zichtbaar.
namespace AalstAcademie.Web.Models.Training;

/// <summary>Formulierdata en scoped leesfeedback; actieflags worden bij iedere write opnieuw servermatig gecontroleerd.</summary>
public sealed class TrainingMomentCreateViewModel : TrainingMomentCreateInput
{
    public int TrainingId { get; set; }
    public string TrainingTitle { get; set; } = "";
    public IReadOnlyList<TrainingAvailabilityRoomReadModel> Rooms { get; set; } = [];
}
public sealed class TrainingMomentEditViewModel : TrainingMomentEditInput
{
    public TrainingMomentDetailsReadModel Details { get; set; } = null!;
    public IReadOnlyList<TrainingAvailabilityRoomReadModel> Rooms { get; set; } = [];
}
public sealed class TrainingMomentCapacityViewModel : TrainingMomentCapacityInput
{
    public TrainingMomentDetailsReadModel Details { get; set; } = null!;
}
/// <summary>De beschikbaarheidslezing reserveert niets; alleen de afzonderlijke POST mag daadwerkelijk boeken.</summary>
public sealed class TrainingMomentAvailabilityViewModel : TrainingMomentAvailabilityInput
{
    public int TrainingId { get; set; }
    public int? MomentId { get; set; }
    public string TrainingTitle { get; set; } = "";
    public bool CanEdit { get; set; } = true;
    public IReadOnlyList<TrainingAvailabilityRoomReadModel> Rooms { get; set; } = [];
}
