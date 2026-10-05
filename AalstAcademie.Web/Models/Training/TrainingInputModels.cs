// Sprint 003: Whitelist en strikte parsing beperken metadata- en doelgroepinvoer
// Aanwezige prijs/motivatievelden worden onderscheiden van ontbrekende velden om bestaande voorwaarden te bewaren.
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AalstAcademie.Web.Models.Training;

/// <summary>
/// Alleen opleidingsmetadata. Referenties en bedragen blijven tekst totdat strikte parsing slaagt,
/// zodat ook foutieve invoer zonder afronding of fallback opnieuw zichtbaar blijft.
/// </summary>
public class TrainingFieldsInput
{
    public const string BindFields = "Title,Description,CategoryId,ExternalTotalPriceEuros,RequiresMotivation";
    [Display(Name = "Titel")] public string? Title { get; set; }
    [Display(Name = "Beschrijving")] public string? Description { get; set; }
    [Display(Name = "Categorie")] public string? CategoryId { get; set; }
    [Display(Name = "Totale externe prijs in EUR")] public string? ExternalTotalPriceEuros { get; set; }
    [Display(Name = "Motivatie verplicht bij inschrijving")] public bool RequiresMotivation { get; set; }
}

/// <summary>Alleen een beheerder kiest een vaste eigenaar; iedere nieuwe opleiding begint zonder toegewezen doelgroep.</summary>
public class TrainingCreateInput : TrainingFieldsInput
{
    public const string BindCreate = BindFields + ",OwnerUserId";
    [Display(Name = "Lesgever")] public string? OwnerUserId { get; set; }
}

/// <summary>Een gewone wijziging kan geen eigenaar, doelgroep of archiefstatus binden.</summary>
public class TrainingEditInput : TrainingFieldsInput
{
    public const string BindEdit = BindFields + ",ExpectedVersion";
    public string? ExpectedVersion { get; set; }
}

/// <summary>Doelgroep is een aparte beheerdercommand met de bewust gelezen versie en volledige gewenste linkset.</summary>
public class TrainingAudienceInput
{
    public const string BindAudience = "ExpectedVersion,AudienceScope,DepartmentIds";
    public string? ExpectedVersion { get; set; }
    [Display(Name = "Doelgroep")] public string? AudienceScope { get; set; }
    public string[] DepartmentIds { get; set; } = [];
}

/// <summary>Strikte HTTP-parsing deelt geldregels met de services en bewaart de oorspronkelijke ModelState-waarden.</summary>
internal static class TrainingInputParsing
{
    public static TrainingFields Fields(TrainingFieldsInput input, ModelStateDictionary state)
    {
        var category = Integer(input.CategoryId, "CategoryId", state);
        decimal? price = null;
        if (!string.IsNullOrWhiteSpace(input.ExternalTotalPriceEuros) &&
            !TrainingValueValidation.TryParseMoney(input.ExternalTotalPriceEuros, out price))
            state.AddModelError("ExternalTotalPriceEuros", "Vul een niet-negatieve EUR-prijs in met hoogstens twee decimalen en zonder duizendseparator.");
        return new(input.Title, input.Description, category, price, input.RequiresMotivation);
    }

    /// <summary>Iedere mutatie vereist een niet-lege Guid; ook ontbrekende invoer geeft HTTP400 vóór een serviceaanroep.</summary>
    public static Guid? Version(string? raw, ModelStateDictionary state)
    {
        if (Guid.TryParse(raw, out var version) && version != Guid.Empty) return version;
        state.AddModelError("ExpectedVersion", "Het versietoken is ongeldig. Herlaad de gegevens bewust.");
        return null;
    }

    /// <summary>De afzonderlijke doelgroepactie parseert een gesloten enum; de service weigert terugzetten naar Unassigned.</summary>
    public static TrainingAudienceScope? Audience(string? raw, ModelStateDictionary state, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (required) state.AddModelError("AudienceScope", "Kies alle afdelingen of geselecteerde afdelingen.");
            return null;
        }
        // Enum.TryParse accepteert ook kommagecombineerde namen; deze gesloten lijst doet dat bewust niet.
        var value = raw.Trim().ToLowerInvariant() switch
        {
            "0" or "unassigned" => TrainingAudienceScope.Unassigned,
            "1" or "alldepartments" => TrainingAudienceScope.AllDepartments,
            "2" or "selecteddepartments" => TrainingAudienceScope.SelectedDepartments,
            _ => (TrainingAudienceScope?)null
        };
        if (value is null) state.AddModelError("AudienceScope", "Kies een geldige doelgroep.");
        return value;
    }

    /// <summary>Parseert elke checkboxwaarde afzonderlijk; dubbele nummers blijven staan voor servicevalidatie.</summary>
    public static int[] Departments(IEnumerable<string> raw, ModelStateDictionary state) => raw
        .Select(value => Integer(value, "DepartmentIds", state)).Where(value => value is not null).Select(value => value!.Value).ToArray();

    /// <summary>Accepteert uitsluitend een Int32-geheel getal; positiviteit en referentiebestaan toetst de service.</summary>
    public static int? Integer(string? raw, string field, ModelStateDictionary state)
    {
        if (int.TryParse(raw?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) return value;
        state.AddModelError(field, "Vul een geldig geheel getal binnen het ondersteunde bereik in.");
        return null;
    }

    /// <summary>Whitelist én multipliciteit beschermen ook velden die modelbinding anders stil negeert.</summary>
    public static async Task CheckFormAsync(HttpRequest request, ModelStateDictionary state, string permitted,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            state.AddModelError(string.Empty, "Verstuur deze actie via het formulier.");
            return;
        }
        var allowed = permitted.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
        var form = await request.ReadFormAsync(cancellationToken);
        foreach (var (key, values) in form)
        {
            if (key.Equals("__RequestVerificationToken", StringComparison.OrdinalIgnoreCase)) continue;
            if (!allowed.Contains(key))
            {
                state.AddModelError(key, "Dit veld mag niet via deze actie worden gewijzigd.");
                continue;
            }
            if (key.Equals("DepartmentIds", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("RequiresMotivation", StringComparison.OrdinalIgnoreCase))
            {
                // De standaard checkbox post true + hidden false; andere dubbele scalarwaarden zijn ambigu.
                var valid = values.Count == 1 && (values[0]?.Equals("true", StringComparison.OrdinalIgnoreCase) == true || values[0]?.Equals("false", StringComparison.OrdinalIgnoreCase) == true)
                    || values.Count == 2 && values[0]?.Equals("true", StringComparison.OrdinalIgnoreCase) == true && values[1]?.Equals("false", StringComparison.OrdinalIgnoreCase) == true;
                if (!valid)
                {
                    if (state.TryGetValue(key, out var entry)) entry?.Errors.Clear();
                    state.AddModelError(key, "Kies een geldige motivatie-instelling.");
                }
            }
            else if (values.Count != 1) state.AddModelError(key, "Verstuur dit veld precies één keer.");
        }
    }

    /// <summary>Kopieert alleen veilige invoer, nooit actorgegevens of nieuwe versies, naar een herweergavemodel.</summary>
    public static void CopyFields(TrainingFieldsInput source, TrainingFieldsInput target)
    {
        target.Title = source.Title; target.Description = source.Description; target.CategoryId = source.CategoryId;
        target.ExternalTotalPriceEuros = source.ExternalTotalPriceEuros; target.RequiresMotivation = source.RequiresMotivation;
    }
}
