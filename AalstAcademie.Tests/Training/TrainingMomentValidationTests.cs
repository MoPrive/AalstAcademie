// Sprint 003: Toetst verplichte datum, begin/einde, zaal-ID en positief maximum
// Beschikbaarheid en actuele zaalcapaciteit worden pas door de schrijfservice gecontroleerd.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Services.Training;

namespace AalstAcademie.Tests.Training;

/// <summary>
/// Vier pure cases bewaken de scalaire momentinvoer. De service leest zaalcapaciteit,
/// actuele eigenaar en eventuele overlap pas onder de gezamenlijke schrijfgrens.
/// </summary>
public class TrainingMomentValidationTests
{
    // Een positief maximum is vereist; zaalcapaciteit is een actuele referentieregel van de service.
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(int.MaxValue, true)]
    public void Maximum_participants_requires_a_positive_int(int maximum, bool expected)
    {
        var validation = Validation();
        var fields = ValidFields() with { MaximumParticipants = maximum };
        var result = validation.ValidateMomentFields(fields);
        Assert.Equal(expected, result.Succeeded);
        if (expected)
        {
            Assert.Equal(maximum, result.Fields!.MaximumParticipants);
            Assert.Equal(fields.Date, result.Fields.Date);
            Assert.Equal(fields.StartTime!.Value.Ticks, result.Fields.StartTime.Ticks);
            Assert.Equal(fields.EndTime!.Value.Ticks, result.Fields.EndTime.Ticks);
            Assert.Equal(fields.LocationId, result.Fields.LocationId);
            // Een geldig verledenmoment aanmaken is toegestaan; editbaarheid is een apart contract.
            Assert.True(validation.ValidateMomentFields(fields with { Date = new DateOnly(2026, 1, 15) }).Succeeded);
        }
        else { Assert.Contains("MaximumParticipants", result.Errors.Keys); Assert.Null(result.Fields); }
        var omitted = validation.ValidateMomentFields(fields with { MaximumParticipants = null });
        Assert.False(omitted.Succeeded);
        Assert.Contains("MaximumParticipants", omitted.Errors.Keys);
    }

    // Ontbrekende/ongeldige IDs krijgen geen impliciete eerste zaal; bestaan wordt daarna echt herlezen.
    [Fact]
    public void Location_id_has_no_default_or_repair()
    {
        var validation = Validation();
        foreach (var invalid in new int?[] { null, 0, -1 })
        {
            var result = validation.ValidateMomentFields(ValidFields() with { LocationId = invalid });
            Assert.False(result.Succeeded);
            Assert.Contains("LocationId", result.Errors.Keys);
            Assert.Null(result.Fields);
        }
        var accepted = validation.ValidateMomentFields(ValidFields());
        Assert.True(accepted.Succeeded);
        Assert.Equal(1, accepted.Fields!.LocationId);
    }

    private static TrainingMomentFields ValidFields() => new(new DateOnly(2027, 6, 15),
        new TimeOnly(9, 15, 30).Add(TimeSpan.FromTicks(1234567)),
        new TimeOnly(10, 15, 30).Add(TimeSpan.FromTicks(7654321)), 1, 1);
    private static TrainingValueValidation Validation() => new(new TrainingSchedule(new FixedTimeProvider()));
}
