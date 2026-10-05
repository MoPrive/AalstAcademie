// Sprint 003: Toetst titel, tekst, categorie, externe totaalprijs, motivatiekeuze en doelgroep
// Bedragen worden zonder afronding of duizendseparator geparseerd.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;

namespace AalstAcademie.Tests.Training;

/// <summary>
/// Tweeëndertig pure cases bewaken de veldcontracten zonder HTTP, database of systeemklok.
/// Een grensmatrix binnen één Fact/Theoryrij telt als één case, niet als extra assertions.
/// </summary>
public class TrainingValidationTests
{
    // V1: de gedeelde titel blijft getrimde tekst; moment-/zaalvelden zijn afzonderlijke contracten.
    [Fact]
    public void Required_text_is_trimmed_and_bounded()
    {
        var validation = Validation();
        var boundary = new string('x', 200);
        var accepted = validation.ValidateFields(ValidFields() with { Title = "  " + boundary + "  " }, false);
        Assert.True(accepted.Succeeded);
        Assert.Equal(boundary, accepted.Fields!.Title);
        foreach (var invalid in new string?[] { null, " \t\r\n", new string('x', 201), "Tekst\0achter NUL" })
        {
            var result = validation.ValidateFields(ValidFields() with { Title = invalid }, false);
            Assert.False(result.Succeeded);
            Assert.Contains("Title", result.Errors.Keys);
            Assert.Null(result.Fields);
        }
    }

    // V2: gewone tekst blijft gewone tekst; optioneel betekent niet onbeperkte lengte.
    [Theory]
    [InlineData("null", true)]
    [InlineData("empty", true)]
    [InlineData("whitespace", true)]
    [InlineData("multiline-html", true)]
    [InlineData("4000", true)]
    [InlineData("4001", false)]
    public void Description_is_optional_plain_text(string variant, bool expected)
    {
        var description = variant switch
        {
            "null" => null, "empty" => "", "whitespace" => " \t\r\n",
            "multiline-html" => "Eerste lijn\n<script>gewone tekst</script>",
            "4000" => new string('x', 4000), _ => new string('x', 4001)
        };
        if (variant == "4001") foreach (var nul in new[] { "tekst\0suffix", "tekst\0" + new string('x', 4001) })
        {
            var rejected = Validation().ValidateFields(ValidFields() with { Description = nul }, false);
            Assert.False(rejected.Succeeded); Assert.Contains("Description", rejected.Errors.Keys); Assert.Null(rejected.Fields);
        }
        var result = Validation().ValidateFields(ValidFields() with { Description = description }, false);
        Assert.Equal(expected, result.Succeeded);
        if (!expected) { Assert.Contains("Description", result.Errors.Keys); Assert.Null(result.Fields); return; }
        Assert.Equal(string.IsNullOrWhiteSpace(description) ? null : description, result.Fields!.Description);
    }

    // V3: tekstparsing en exacte integercenten worden apart bewezen; niets wordt afgerond.
    [Theory]
    [InlineData(null, false, 0L)]
    [InlineData("-0.01", false, 0L)]
    [InlineData("0", true, 0L)]
    [InlineData("0.01", true, 1L)]
    [InlineData("0,01", true, 1L)]
    [InlineData("12.30", true, 1230L)]
    [InlineData("12,30", true, 1230L)]
    [InlineData("92233720368547758.07", true, long.MaxValue)]
    [InlineData("92233720368547758.08", false, 0L)]
    [InlineData("1.001", false, 0L)]
    [InlineData("1e2", false, 0L)]
    [InlineData("1,234.56", false, 0L)]
    public void Money_text_requires_exact_supported_cents(string? text, bool expected, long expectedCents)
    {
        Assert.Equal(expected, TrainingValueValidation.TryParseMoney(text, out var euros));
        if (!expected) { Assert.Null(euros); return; }
        Assert.NotNull(euros);
        Assert.True(TrainingValueValidation.TryGetExactCents(euros.Value, out var cents));
        Assert.Equal(expectedCents, cents);
        Assert.Equal(euros.Value, cents / 100m);
    }

    // V4: de opgeslagen eigenaar bepaalt de prijsvorm; null is iets anders dan expliciet gratis.
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Price_shape_matches_owner(bool externalOwner, bool pricePresent, bool expected)
    {
        var result = Validation().ValidateFields(ValidFields() with
            { ExternalTotalPriceEuros = pricePresent ? 0m : null }, externalOwner);
        Assert.Equal(expected, result.Succeeded);
        if (!expected) Assert.Contains("ExternalTotalPriceEuros", result.Errors.Keys);
    }

    // V5: ook alle huidige afdelingen selecteren blijft Selected; er is geen impliciete All-default.
    [Theory]
    [InlineData("unassigned-empty", true)]
    [InlineData("all-empty", true)]
    [InlineData("selected-one", true)]
    [InlineData("selected-all-current", true)]
    [InlineData("selected-empty", false)]
    [InlineData("all-with-links", false)]
    [InlineData("unassigned-with-links", false)]
    [InlineData("unknown-enum", false)]
    public void Audience_shape_is_explicit(string variant, bool expected)
    {
        var scope = variant.StartsWith("selected", StringComparison.Ordinal) ? TrainingAudienceScope.SelectedDepartments
            : variant.StartsWith("all", StringComparison.Ordinal) ? TrainingAudienceScope.AllDepartments
            : variant == "unknown-enum" ? (TrainingAudienceScope)99 : TrainingAudienceScope.Unassigned;
        int[] ids = variant == "selected-all-current" ? [1, 2, 3]
            : variant is "selected-one" or "all-with-links" or "unassigned-with-links" ? [1] : [];
        var result = TrainingValueValidation.ValidateAudience(scope, ids);
        Assert.Equal(expected, result.Succeeded);
        if (expected) { Assert.Equal(scope, result.Scope); Assert.Equal(ids, result.DepartmentIds); }
        else Assert.NotEmpty(result.Errors);
    }

    // V6: dezelfde Unicode-naam krijgt dezelfde opslagnormalisatie, zonder SQLite NOCASE te vertrouwen.
    [Fact]
    public void Category_normalization_is_unicode_stable()
    {
        var first = TrainingValueValidation.ValidateCategoryName(" \u2003café\t  Onderzoek\u00a0 ");
        var equivalent = TrainingValueValidation.ValidateCategoryName("Cafe\u0301 \r\n Onderzoek");
        Assert.True(first.Succeeded);
        Assert.True(equivalent.Succeeded);
        Assert.Equal("café Onderzoek", first.Name);
        Assert.Equal("CAFÉ ONDERZOEK", first.NormalizedName);
        Assert.Equal(first.NormalizedName, equivalent.NormalizedName);
    }

    /// <summary>Volledige fictieve invoer; referentiebestaan en toegang behoren tot de servicetests.</summary>
    private static TrainingFields ValidFields() => new("Testopleiding", null, 1, null, false);

    private static TrainingValueValidation Validation() => new(new TrainingSchedule(new FixedTimeProvider()));
}
