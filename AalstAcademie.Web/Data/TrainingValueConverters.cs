// Sprint 003: Bewaart geld als gehele centen, datums canoniek, tijden als exacte ticks en historie als UTC-tekst
// Daarmee blijft de CLR-betekenis na een SQLite-readback gelijk.
using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AalstAcademie.Web.Data;

/// <summary>
/// Bewaart de goedgekeurde CLR-vormen zonder SQLite-REAL of tijdprecisieverlies.
/// Nullable prijswaarden blijven door EF null; deze converters behandelen alleen aanwezige waarden.
/// </summary>
public static class TrainingValueConverters
{
    public const decimal MaximumExternalTotalPriceEuros = 92233720368547758.07m;

    public static readonly ValueConverter<DateOnly, string> DateToText = new(
        value => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        value => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None));

    public static readonly ValueConverter<TimeOnly, long> TimeToTicks = new(
        value => value.Ticks, value => new TimeOnly(value));

    public static readonly ValueConverter<decimal, long> ExactEuroCents = new(
        value => EuroToCents(value), value => CentsToEuro(value));

    // Eén vaste UTC-vorm sorteert chronologisch en leest expliciet DateTimeKind.Utc terug.
    // Accountreview-/Identitytijdvelden behouden hun bestaande converters.
    public static readonly ValueConverter<DateTime, string> UtcToText = new(
        value => FormatUtc(value), value => ParseUtc(value));

    // Een ontbrekende sluitingsdatum blijft null; EF geeft null niet aan de gewone converter door.
    public static readonly ValueConverter<DateTime?, string?> NullableUtcToText = new(
        value => value.HasValue ? FormatUtc(value.Value) : null,
        value => value == null ? null : ParseUtc(value));

    /// <summary>Weigert Local/Unspecified: de schrijver moet vóór opslag zijn UTC-keuze expliciet maken.</summary>
    public static string FormatUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("De historiedatum moet expliciet UTC zijn.", nameof(value));
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>De vaste precisie blijft behouden; de Z-literal levert samen met de styles expliciete UTC-kind.</summary>
    public static DateTime ParseUtc(string value) => DateTime.ParseExact(value,
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>
    /// Rekent alleen exact gehele centen om. Negativiteit/typevorm horen bij servicevalidatie
    /// en de opslag-CHECK; onrepresentabele fracties en Int64-overflow worden nooit afgerond.
    /// </summary>
    public static long EuroToCents(decimal euros)
    {
        var cents = euros * 100m;
        if (cents != decimal.Truncate(cents))
            throw new ArgumentException("De prijs moet exact in eurocenten uitgedrukt kunnen worden.", nameof(euros));
        return checked((long)cents);
    }

    /// <summary>Deelt integercenten exact door honderd met decimal, zonder binair drijvendekommagetal.</summary>
    public static decimal CentsToEuro(long cents) => cents / 100m;
}
