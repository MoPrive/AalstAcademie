using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Services.Enrolment;

namespace AalstAcademie.Tests.Enrolment;

/// <summary>De concrete invoergrenzen komen uit het contract; geen providerwrite bij ongeldige invoer.</summary>
public class EnrolmentValidationTests
{
    [Theory]
    [InlineData("OptionalNull")]
    [InlineData("OptionalWhitespaceToNull")]
    [InlineData("TrimNonempty")]
    [InlineData("RequiredNull")]
    [InlineData("RequiredWhitespace")]
    [InlineData("Exactly4000Accepted")]
    [InlineData("4001Rejected")]
    [InlineData("NulRejected")]
    [InlineData("GuidEmptyTrainingRejected")]
    [InlineData("GuidEmptyMomentRejected")]
    [InlineData("ExplicitConsentRequired")]
    [InlineData("EncodedTextStoredAsText")]
    public void Contract(string variant)
    {
        if (variant.StartsWith("GuidEmpty", StringComparison.Ordinal))
        {
            var input = new EmployeeRequestInput
            {
                ExpectedTrainingVersion = variant == "GuidEmptyTrainingRejected" ? Guid.Empty : Guid.NewGuid(),
                ExpectedMomentVersion = variant == "GuidEmptyMomentRejected" ? Guid.Empty : Guid.NewGuid()
            };
            Assert.NotEmpty(ParticipationEligibility.ValidateVersions(input)); return;
        }
        if (variant == "ExplicitConsentRequired")
        {
            var input = new EmployeeWaitlistInput { ExpectedTrainingVersion = Guid.NewGuid(), ExpectedMomentVersion = Guid.NewGuid() };
            Assert.Contains("Consent", ParticipationEligibility.ValidateWaitlistInput(input).Keys); return;
        }
        var raw = variant switch
        {
            "OptionalWhitespaceToNull" or "RequiredWhitespace" => " \t\n ",
            "TrimNonempty" => "  Mijn reden  ",
            "Exactly4000Accepted" => new string('x', 4000),
            "4001Rejected" => new string('x', 4001),
            "NulRejected" => "reden\0rest",
            "EncodedTextStoredAsText" => "<script>voorbeeld</script>",
            _ => null
        };
        var result = ParticipationEligibility.ValidateMotivation(raw, variant.StartsWith("Required", StringComparison.Ordinal));
        var invalid = variant is "RequiredNull" or "RequiredWhitespace" or "4001Rejected" or "NulRejected";
        Assert.Equal(!invalid, result.Succeeded);
        if (invalid) Assert.Contains("Motivation", result.Errors.Keys);
        else Assert.Equal(string.IsNullOrWhiteSpace(raw) ? null : raw.Trim(), result.Value);
    }
}
