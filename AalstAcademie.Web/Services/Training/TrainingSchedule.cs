// Sprint 003: Parseert Belgische kalenderplanning en zet valide lokale eindpunten expliciet naar UTC om
// Onmogelijke/dubbelzinnige DST-tijden en ontbrekende zonedata worden geweigerd.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using AalstAcademie.Web.Models.Domain;

namespace AalstAcademie.Web.Services.Training;

/// <summary>Lokale CLR-waarden en hun expliciete Belgische UTC-eindpunten; geen host-local conversie.</summary>
public sealed record TrainingScheduleValue(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>Planning wordt alleen bruikbaar als alle verplichte waarden en beide DST-eindpunten geldig zijn.</summary>
public sealed record TrainingScheduleValidation(TrainingScheduleValue? Value,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool Succeeded => Value is not null && Errors.Count == 0;
}

/// <summary>Belgische planning, strikte tekstparsing en actuele editgrens; TimeProvider maakt tijd toetsbaar.</summary>
public sealed class TrainingSchedule
{
    private readonly TimeProvider clock;
    public TimeZoneInfo TimeZone { get; }

    /// <summary>Ontbrekende/ongeldige zonedata zijn een configuratiefout; er is geen stil UTC-default.</summary>
    public TrainingSchedule(TimeProvider clock, Func<string, TimeZoneInfo>? resolver = null)
    {
        this.clock = clock;
        resolver ??= TimeZoneInfo.FindSystemTimeZoneById;
        try { TimeZone = resolver("Europe/Brussels"); }
        catch (Exception first) when (first is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            try { TimeZone = resolver("Romance Standard Time"); }
            catch (Exception fallback) when (fallback is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new InvalidOperationException("De Belgische tijdzone kon niet worden geladen; controleer de configuratie.", fallback);
            }
        }
    }

    /// <summary>Eén kalenderdag/einde na begin; verledenaanmaak wordt niet door de actuele klok verboden.</summary>
    public TrainingScheduleValidation Validate(DateOnly? date, TimeOnly? start, TimeOnly? end)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (date is null) errors["Date"] = ["Vul een geldige datum in."];
        if (start is null) errors["StartTime"] = ["Vul een geldig beginuur in."];
        if (end is null) errors["EndTime"] = ["Vul een geldig einduur in."];
        if (start is not null && end is not null && end <= start)
            errors["EndTime"] = ["Het einduur moet op dezelfde dag na het beginuur liggen."];
        DateTimeOffset startUtc = default, endUtc = default;
        if (date is not null && start is not null && !TryUtc(date.Value, start.Value, out startUtc, out var startError))
            errors["StartTime"] = [startError!];
        if (date is not null && end is not null && !TryUtc(date.Value, end.Value, out endUtc, out var endError))
            errors["EndTime"] = [endError!];
        var frozen = new ReadOnlyDictionary<string, string[]>(errors);
        return errors.Count == 0
            ? new(new TrainingScheduleValue(date!.Value, start!.Value, end!.Value, startUtc, endUtc), frozen)
            : new(null, frozen);
    }

    /// <summary>Leest iedere aanroep opnieuw de klok; de service toetst na write-lock én vlak vóór mutatie.</summary>
    public bool IsBeforeStoredStart(DateOnly date, TimeOnly start) =>
        TryUtc(date, start, out var storedStartUtc, out _) && clock.GetUtcNow() < storedStartUtc;

    /// <summary>Alleen Scheduled vóór de opgeslagen start; geposte nieuwe planning kan deze grens niet verplaatsen.</summary>
    public bool CanEdit(TrainingMoment moment) => moment.Status == TrainingMomentStatus.Scheduled &&
        IsBeforeStoredStart(moment.Date, moment.StartTime);

    /// <summary>Een zaalmaximum beschermt ieder nog niet geëindigd moment; exact einde is niet meer blokkerend.</summary>
    public bool IsBeforeStoredEnd(DateOnly date, TimeOnly end) =>
        TryUtc(date, end, out var storedEndUtc, out _) && clock.GetUtcNow() < storedEndUtc;

    /// <summary>Alleen canonieke kalenderdatums; ontbrekende/malformed tekst krijgt geen DateOnly-default.</summary>
    public static bool TryParseDate(string? raw, out DateOnly? date)
    {
        date = null;
        var text = raw?.Trim();
        if (text is null || !Regex.IsMatch(text, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z") ||
            !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        date = parsed;
        return true;
    }

    /// <summary>HH:mm of HH:mm:ss met maximaal zeven fractiecijfers; de ticks blijven exact bewaard.</summary>
    public static bool TryParseTime(string? raw, out TimeOnly? time)
    {
        time = null;
        var text = raw?.Trim();
        if (text is null || !Regex.IsMatch(text, @"\A(?:[01][0-9]|2[0-3]):[0-5][0-9](?::[0-5][0-9](?:\.[0-9]{1,7})?)?\z") ||
            !TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        time = parsed;
        return true;
    }

    private bool TryUtc(DateOnly date, TimeOnly time, out DateTimeOffset utc, out string? error)
    {
        utc = default;
        error = null;
        // Unspecified betekent Belgische lokale tijd; Local/UTC zou de hostzone in de berekening mengen.
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (TimeZone.IsInvalidTime(local)) { error = "Dit uur bestaat niet bij de Belgische zomeruurwisseling."; return false; }
        if (TimeZone.IsAmbiguousTime(local)) { error = "Dit uur komt tweemaal voor bij de Belgische winteruurwisseling."; return false; }
        utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, TimeZone));
        return true;
    }
}
