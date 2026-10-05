using System.Net;
using AalstAcademie.Tests.Infrastructure;
namespace AalstAcademie.Tests.Web;

public sealed class RegistrationManagementHttpTests
{
    [Theory]
    [InlineData("AdminAll")]
    [InlineData("OwnerConfirmPrg")]
    [InlineData("OwnerRefuseConfirmed403")]
    [InlineData("Employee403")]
    [InlineData("ForeignDetail404")]
    [InlineData("MissingAntiforgery400")]
    [InlineData("OverpostedActor400")]
    [InlineData("MalformedVersion400")]
    [InlineData("StaleRegistration409KeepsTokensAndReason")]
    [InlineData("SqlFailure503PreservesInput")]
    [InlineData("NativeFormLabelsAndActionsInHtml")]
    public Task Cases(string variant) => ParticipationWorkflowHttpChecks.RunAsync("review", variant);
}
