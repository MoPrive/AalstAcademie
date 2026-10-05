// Sprint 003: Bewaart datum, tijd en maximum als raw tekst voor strikte parsing en behoud bij fouten
// Create, planningedit, capaciteit en availability hebben verschillende veldcontracten.
using System.ComponentModel.DataAnnotations;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AalstAcademie.Web.Models.Training;

/// <summary>Ruwe Belgische planning blijft tekst: fouten en volledige tijdfracties gaan niet verloren bij herweergave.</summary>
public class TrainingMomentPlanningInput
{
    public const string BindPlanning = "Date,StartTime,EndTime,LocationId";
    [Display(Name = "Datum (JJJJ-MM-DD)")] public string? Date { get; set; }
    [Display(Name = "Begintijd")] public string? StartTime { get; set; }
    [Display(Name = "Eindtijd")] public string? EndTime { get; set; }
    [Display(Name = "Zaal")] public string? LocationId { get; set; }
}
/// <summary>Alleen bij aanmaak worden planning en deelnemersmaximum samen gekozen.</summary>
public class TrainingMomentCreateInput : TrainingMomentPlanningInput
{
    public const string BindCreate = BindPlanning + ",MaximumParticipants";
    [Display(Name = "Maximum deelnemers")] public string? MaximumParticipants { get; set; }
}
/// <summary>Planningedit kan het maximum, de opleiding, eigenaar en status niet binden.</summary>
public class TrainingMomentEditInput : TrainingMomentPlanningInput
{
    public const string BindEdit = BindPlanning + ",ExpectedVersion";
    public string? ExpectedVersion { get; set; }
}
/// <summary>Capaciteit is een afzonderlijke command zonder planningvelden.</summary>
public class TrainingMomentCapacityInput
{
    public const string BindCapacity = "MaximumParticipants,ExpectedVersion";
    [Display(Name = "Maximum deelnemers")] public string? MaximumParticipants { get; set; }
    public string? ExpectedVersion { get; set; }
}
/// <summary>De GET voor zaalkeuze draagt planning en oude versie mee, maar geen CSRF-token of schrijfbevoegdheid.</summary>
public class TrainingMomentAvailabilityInput : TrainingMomentCreateInput
{
    public string? ExpectedVersion { get; set; }
}
/// <summary>HTTP-scalars worden strikt geparseerd vóór de dienst; kalender, DST en races blijven ook serviceguards.</summary>
internal static class MomentInputParsing
{
    // Voorkom een default kalenderdatum bij onleesbare invoer; ModelState bewaart de fout en de oorspronkelijke tekst.
    public static DateOnly? Date(string? raw, ModelStateDictionary state)
    {
        if (TrainingSchedule.TryParseDate(raw, out var value)) return value;
        state.AddModelError("Date", "Vul een geldige datum in als JJJJ-MM-DD."); return null;
    }
    // Behoud volledige tijdprecisie en laat dezelfde strikte parser als de domeinvalidatie beslissen.
    public static TimeOnly? Time(string? raw, string key, ModelStateDictionary state)
    {
        if (TrainingSchedule.TryParseTime(raw, out var value)) return value;
        state.AddModelError(key, "Gebruik UU:mm of UU:mm:ss met hoogstens zeven fractiecijfers."); return null;
    }
    // Een geldig geheel getal moet ook positief zijn; null of nul mag niet stil een bruikbaar deelnemersmaximum worden.
    public static int? Positive(string? raw, string key, ModelStateDictionary state)
    {
        var value = TrainingInputParsing.Integer(raw, key, state);
        if (value is <= 0) state.AddModelError(key, "Vul een positief geheel getal in.");
        return value;
    }
    public static TrainingMomentPlanningFields Planning(TrainingMomentPlanningInput input, ModelStateDictionary state) =>
        new(Date(input.Date, state), Time(input.StartTime, "StartTime", state), Time(input.EndTime, "EndTime", state), Positive(input.LocationId, "LocationId", state));
}
