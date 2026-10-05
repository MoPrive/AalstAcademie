using System.Net;
using AalstAcademie.Tests.Infrastructure;
namespace AalstAcademie.Tests.Web;

public sealed class TrainingMomentCancellationHttpTests
{
    [Theory]
    [InlineData("AdminConfirmationCountsAndWarning")]
    [InlineData("Instructor403")]
    [InlineData("MissingAntiforgery400")]
    [InlineData("OverpostedTraining400")]
    [InlineData("Stale409OriginalToken")]
    [InlineData("SqlFailure503CompleteRollback")]
    [InlineData("SuccessPrgReturnsBuffer")]
    [InlineData("NoRefillOtherMoment")]
    [InlineData("ConfirmationLabelsAndActionsHtml")]
    public Task Cases(string variant) => ParticipationWorkflowHttpChecks.RunAsync("moment", variant);
}
