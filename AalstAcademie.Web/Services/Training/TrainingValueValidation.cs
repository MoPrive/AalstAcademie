// Sprint 003: Normaliseert tekst en controleert geld, doelgroep, zaal en momentvelden zonder stille defaults of afronding
// Referentiebestaan, actuele rechten en concurrerende writes horen bij de services.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AalstAcademie.Web.Models.Domain;

namespace AalstAcademie.Web.Services.Training;

/// <summary>De volledig gevalideerde gewone velden; referenties en rechten controleert de service apart.</summary>
public sealed record ValidatedTrainingFields(string Title, string? Description, int CategoryId,
    decimal? ExternalTotalPriceEuros, bool RequiresMotivation);

/// <summary>Een fout produceert geen gedeeltelijk bruikbare velden die per ongeluk toch opgeslagen worden.</summary>
public sealed record TrainingFieldsValidation(ValidatedTrainingFields? Fields,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool Succeeded => Fields is not null && Errors.Count == 0;
}

/// <summary>Gevalideerde zaalvelden bevatten uitsluitend de gekozen gewone scalarwaarden.</summary>
public sealed record ValidatedLocationFields(string Name, string? Address, int MaximumCapacity);

/// <summary>Geen gedeeltelijke zaalwaarden bij een fout; referenties en rechten blijven servicewerk.</summary>
public sealed record LocationFieldsValidation(ValidatedLocationFields? Fields,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool Succeeded => Fields is not null && Errors.Count == 0;
}

/// <summary>Gevalideerde planning blijft exact in CLR-datum/ticks; er is geen vrij bindbare status of eigenaar.</summary>
public sealed record ValidatedTrainingMomentFields(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime,
    int LocationId, int MaximumParticipants);

/// <summary>Planning en positieve scalars worden samen bruikbaar, zonder zaalbestaan/overlap te suggereren.</summary>
public sealed record TrainingMomentFieldsValidation(ValidatedTrainingMomentFields? Fields,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool Succeeded => Fields is not null && Errors.Count == 0;
}

/// <summary>Naam en genormaliseerde unieke sleutel worden als één validatieresultaat geleverd.</summary>
public sealed record CategoryNameValidation(string? Name, string? NormalizedName,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool Succeeded => Name is not null && NormalizedName is not null && Errors.Count == 0;
}

/// <summary>Bewijst alleen de lokale scope/linkvorm; de echte afdelings-FKs worden binnen de transactie gelezen.</summary>
public sealed record TrainingAudienceValidation(TrainingAudienceScope Scope, IReadOnlyList<int> DepartmentIds,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>Gedeelde veldregels voor directe servicecalls en latere HTTP-binding, zonder databasewrites.</summary>
public sealed class TrainingValueValidation(TrainingSchedule schedule)
{
    private const decimal MaximumEuros = long.MaxValue / 100m;

    /// <summary>Valideert gedeelde metadata en prijs; planning staat uitsluitend op het opleidingsmoment.</summary>
    public TrainingFieldsValidation ValidateFields(TrainingFields fields, bool externalOwner)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var title = RequiredText(fields.Title, nameof(fields.Title), "titel", errors);
        var description = string.IsNullOrWhiteSpace(fields.Description) ? null : fields.Description;
        if (description?.Length > 4000 || description?.Contains('\0') == true)
            errors[nameof(fields.Description)] = ["De beschrijving mag maximaal 4000 tekens bevatten, zonder NUL-teken."];
        if (fields.CategoryId is null or <= 0)
            errors[nameof(fields.CategoryId)] = ["Kies een bestaande categorie."];
        if (externalOwner)
        {
            if (fields.ExternalTotalPriceEuros is null || !TryGetExactCents(fields.ExternalTotalPriceEuros.Value, out _))
                errors[nameof(fields.ExternalTotalPriceEuros)] =
                    ["Vul een niet-negatieve totale EUR-prijs in, exact in centen en binnen het ondersteunde bereik."];
        }
        else if (fields.ExternalTotalPriceEuros is not null)
            errors[nameof(fields.ExternalTotalPriceEuros)] = ["Een interne opleiding heeft geen externe prijs."];
        if (errors.Count != 0) return new(null, Freeze(errors));
        return new(new ValidatedTrainingFields(title!, description, fields.CategoryId!.Value,
            fields.ExternalTotalPriceEuros, fields.RequiresMotivation), Freeze(errors));
    }

    /// <summary>Normale zaaltekst en positieve capaciteit, zonder unieke naam of actuele planningsclaims.</summary>
    public LocationFieldsValidation ValidateLocationFields(LocationFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = RequiredText(fields.Name, nameof(fields.Name), "zaalnaam", errors);
        var address = string.IsNullOrWhiteSpace(fields.Address) ? null : fields.Address;
        if (address?.Length > 500 || address?.Contains('\0') == true) errors[nameof(fields.Address)] = ["Het adres mag maximaal 500 tekens bevatten, zonder NUL-teken."];
        if (fields.MaximumCapacity is null or <= 0)
            errors[nameof(fields.MaximumCapacity)] = ["De zaalcapaciteit moet een positief geheel getal zijn."];
        return errors.Count == 0
            ? new(new ValidatedLocationFields(name!, address, fields.MaximumCapacity!.Value), Freeze(errors))
            : new(null, Freeze(errors));
    }

    /// <summary>Lokale momentvorm en Belgische DST; de service herleest zaal, overlap en rechten onder write-lock.</summary>
    public TrainingMomentFieldsValidation ValidateMomentFields(TrainingMomentFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (fields.LocationId is null or <= 0) errors[nameof(fields.LocationId)] = ["Kies een bestaande zaal."];
        if (fields.MaximumParticipants is null or <= 0)
            errors[nameof(fields.MaximumParticipants)] = ["Het deelnemersmaximum moet een positief geheel getal zijn."];
        var timing = schedule.Validate(fields.Date, fields.StartTime, fields.EndTime);
        foreach (var error in timing.Errors) errors[error.Key] = error.Value;
        return errors.Count == 0
            ? new(new ValidatedTrainingMomentFields(timing.Value!.Date, timing.Value.StartTime, timing.Value.EndTime,
                fields.LocationId!.Value, fields.MaximumParticipants!.Value), Freeze(errors))
            : new(null, Freeze(errors));
    }

    /// <summary>Normaliseert Unicode vóór uppercase; SQLite BINARY vergelijkt straks deze expliciete sleutel.</summary>
    public static CategoryNameValidation ValidateCategoryName(string? name)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? normalizedText = null;
        try
        {
            if (name is not null)
                normalizedText = Regex.Replace(name.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ");
        }
        catch (ArgumentException)
        {
            // Ongeldige Unicode is een invoerfout; geen exception of half genormaliseerde categorie opslaan.
            errors["Name"] = ["De categorienaam bevat ongeldige tekst."];
        }
        // NUL kan SQLite length() laten stoppen vóór de werkelijke stringlengte; geen verborgen naamsuffix.
        if (string.IsNullOrWhiteSpace(normalizedText) || normalizedText.Length > 100 || normalizedText.Contains('\0'))
            errors["Name"] = ["Vul een categorienaam van 1 tot en met 100 tekens in, zonder NUL-teken."];
        return errors.Count == 0
            ? new(normalizedText, normalizedText!.ToUpperInvariant(), Freeze(errors))
            : new(null, null, Freeze(errors));
    }

    /// <summary>Afwezig betekent Unassigned; een lege Selected-set of dubbele ID wordt nooit stil hersteld.</summary>
    public static TrainingAudienceValidation ValidateAudience(TrainingAudienceScope? scope,
        IReadOnlyList<int>? ids, bool allowUnassigned = true)
    {
        var selectedScope = scope ?? TrainingAudienceScope.Unassigned;
        var copiedIds = ids?.ToArray() ?? [];
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!Enum.IsDefined(selectedScope)) errors["AudienceScope"] = ["Kies een geldige doelgroep."];
        if (!allowUnassigned && selectedScope == TrainingAudienceScope.Unassigned)
            errors["AudienceScope"] = ["Kies alle afdelingen of minstens één geselecteerde afdeling."];
        if (selectedScope == TrainingAudienceScope.SelectedDepartments)
        {
            if (copiedIds.Length == 0) errors["DepartmentIds"] = ["Selecteer minstens één afdeling."];
        }
        else if (copiedIds.Length != 0)
            errors["DepartmentIds"] = ["Deze doelgroep gebruikt geen afzonderlijke afdelingskoppelingen."];
        if (copiedIds.Any(id => id <= 0) || copiedIds.Distinct().Count() != copiedIds.Length)
            errors["DepartmentIds"] = ["Afdelingsnummers moeten geldig en uniek zijn."];
        // ReadOnlyCollection voorkomt dat een caller de gevalideerde gewenste set daarna ongemerkt verandert.
        return new(selectedScope, Array.AsReadOnly(copiedIds), Freeze(errors));
    }

    /// <summary>Accepteert één decimaalteken, maximaal twee fractiecijfers en uitsluitend exacte niet-negatieve centen.</summary>
    public static bool TryParseMoney(string? text, out decimal? euros)
    {
        euros = null;
        var input = text?.Trim();
        if (input is null || !Regex.IsMatch(input, @"\A[0-9]+(?:[.,][0-9]{1,2})?\z")) return false;
        if (!decimal.TryParse(input.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var amount) || !TryGetExactCents(amount, out _)) return false;
        euros = amount;
        return true;
    }

    /// <summary>Deze gedeelde grens maakt de opslagconversie exact: geen float, afronding of Int64-overflow.</summary>
    public static bool TryGetExactCents(decimal euros, out long cents)
    {
        cents = 0;
        if (euros < 0m || euros > MaximumEuros) return false;
        var exact = euros * 100m;
        if (exact != decimal.Truncate(exact)) return false;
        cents = checked((long)exact);
        return true;
    }

    private static string? RequiredText(string? value, string field, string label,
        IDictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 200 || trimmed.Contains('\0'))
            errors[field] = [$"Vul een {label} van 1 tot en met 200 tekens in, zonder NUL-teken."];
        return trimmed;
    }

    private static IReadOnlyDictionary<string, string[]> Freeze(Dictionary<string, string[]> errors) =>
        new ReadOnlyDictionary<string, string[]>(errors);
}
