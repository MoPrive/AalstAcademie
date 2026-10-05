using System.Net;
using AalstAcademie.Tests.Infrastructure;
namespace AalstAcademie.Tests.Web;

public sealed class RegistrationCancellationHttpTests
{
    [Theory]
    [InlineData("OwnRequestedPrg")]
    [InlineData("OwnConfirmedPrg")]
    [InlineData("OtherOwner404")]
    [InlineData("RoleLossStillApprovedAllowed")]
    [InlineData("MissingAntiforgery400")]
    [InlineData("OverpostedStatus400")]
    [InlineData("StaleRegistration409OriginalTokens")]
    [InlineData("SqlFailure503NoHalfWrite")]
    [InlineData("FormLabelsAndConfirmationHtml")]
    public Task Cases(string variant) => ParticipationWorkflowHttpChecks.RunAsync("own", variant);
}
