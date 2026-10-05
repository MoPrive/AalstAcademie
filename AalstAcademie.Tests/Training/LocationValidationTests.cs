// Sprint 003: Controleert naam, optioneel adres en positief geheel zaalmaximum
// Pure veldvalidatie beslist nog niet over huidige momentreferenties of actorrechten.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Services.Training;

namespace AalstAcademie.Tests.Training;

/// <summary>
/// Vijf pure cases bewaken de invoervorm van een zaal. Beschikbaarheid, bestaande
/// momentreferenties en actuele beheerdersrechten horen bij de schrijfservices.
/// </summary>
public class LocationValidationTests
{
    // Een zaalnaam is gewone getrimde tekst; NUL mag de fysieke lengtecontrole niet omzeilen.
    [Fact]
    public void Name_requires_visible_bounded_text()
    {
        var validation = Validation();
        var boundary = new string('z', 200);
        var accepted = validation.ValidateLocationFields(ValidFields() with { Name = "  " + boundary + "  " });
        Assert.True(accepted.Succeeded);
        Assert.Equal(boundary, accepted.Fields!.Name);
        foreach (var invalid in new string?[] { null, " \t\r\n", new string('z', 201), "Zaal\0verborgen" })
        {
            var result = validation.ValidateLocationFields(ValidFields() with { Name = invalid });
            Assert.False(result.Succeeded);
            Assert.Contains("Name", result.Errors.Keys);
            Assert.Null(result.Fields);
        }
    }

    // Een adres is optioneel; gewone tekst blijft behouden en de grens geldt ook zonder verplichting.
    [Fact]
    public void Optional_address_preserves_text_with_a_finite_limit()
    {
        var validation = Validation();
        foreach (var empty in new string?[] { null, "", " \t\r\n" })
        {
            var result = validation.ValidateLocationFields(ValidFields() with { Address = empty });
            Assert.True(result.Succeeded);
            Assert.Null(result.Fields!.Address);
        }
        foreach (var address in new[] { "Eerste lijn\n<script>gewone tekst</script>", new string('a', 500) })
        {
            var result = validation.ValidateLocationFields(ValidFields() with { Address = address });
            Assert.True(result.Succeeded);
            Assert.Equal(address, result.Fields!.Address);
        }
        foreach (var nul in new[] { "adres\0suffix", "adres\0" + new string('a', 501) })
        {
            var rejected = validation.ValidateLocationFields(ValidFields() with { Address = nul });
            Assert.False(rejected.Succeeded); Assert.Contains("Address", rejected.Errors.Keys); Assert.Null(rejected.Fields);
        }
        var tooLong = validation.ValidateLocationFields(ValidFields() with { Address = new string('a', 501) });
        Assert.False(tooLong.Succeeded);
        Assert.Contains("Address", tooLong.Errors.Keys);
        Assert.Null(tooLong.Fields);
    }

    // Er is alleen een positief geheel getal afgesproken; geen willekeurig zakelijk maximum.
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(int.MaxValue, true)]
    public void Maximum_capacity_requires_a_positive_int(int maximum, bool expected)
    {
        var validation = Validation();
        var result = validation.ValidateLocationFields(ValidFields() with { MaximumCapacity = maximum });
        Assert.Equal(expected, result.Succeeded);
        if (expected) Assert.Equal(maximum, result.Fields!.MaximumCapacity);
        else { Assert.Contains("MaximumCapacity", result.Errors.Keys); Assert.Null(result.Fields); }
        var omitted = validation.ValidateLocationFields(ValidFields() with { MaximumCapacity = null });
        Assert.False(omitted.Succeeded);
        Assert.Contains("MaximumCapacity", omitted.Errors.Keys);
    }

    private static LocationFields ValidFields() => new("Fictieve zaal", null, 1);
    private static TrainingValueValidation Validation() => new(new TrainingSchedule(new FixedTimeProvider()));
}
