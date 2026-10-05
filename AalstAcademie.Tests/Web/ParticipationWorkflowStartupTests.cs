using System.Net.NetworkInformation;
using System.Text.Json;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using Microsoft.Extensions.DependencyInjection;
namespace AalstAcademie.Tests.Web;

/// <summary>Fouten na werkelijke005-SQL houden zowel de gereedhint als de echte Kestrel-listener dicht.</summary>
[Collection("Revision02 provider races")]
public sealed class ParticipationWorkflowStartupTests
{
    [Theory]
    [InlineData("WorkflowReviewFaultNoListenerOrHint")]
    [InlineData("WorkflowRefillFaultNoListenerOrHint")]
    [InlineData("WorkflowCreateFaultNoListenerOrHint")]
    [InlineData("WorkflowJoinFaultNoListenerOrHint")]
    public async Task Cases(string variant)
    {
        var point = variant switch { "WorkflowReviewFaultNoListenerOrHint" => "workflow-review", "WorkflowRefillFaultNoListenerOrHint" => "workflow-refill",
            "WorkflowCreateFaultNoListenerOrHint" => "workflow-create", "WorkflowJoinFaultNoListenerOrHint" => "workflow-join", _ => throw new InvalidOperationException() };
        await using (var e = new DemoStartupTestEnvironment())
        {
            var fault = new DemoStartupFailureInterceptor(); fault.Arm(point);
            await using var provider = e.CreateServices(interceptor: fault); await using var scope = provider.CreateAsyncScope();
            await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>().InitializeAsync());
            Assert.True(fault.Triggered && fault.SawSqlWrite); Assert.False(provider.GetRequiredService<DemoCredentials>().IsReady);
        }
        await using var kestrel = new DemoStartupTestEnvironment(); var hostFault = new DemoStartupFailureInterceptor(); hostFault.Arm(point);
        const int port = 53783;
        Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), x => x.Port == port);
        // Leg het concrete GUID-doel en het eigen testproces vast vóór de echte host start.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(kestrel.DatabasePath)!, "workflow-host-boundary.json"),
            JsonSerializer.Serialize(new { Database = kestrel.DatabasePath, Port = port, OwnTestProcess = Environment.ProcessId }));
        await using var factory = new DemoStartupWebApplicationFactory(kestrel, interceptor: hostFault);
        factory.UseKestrel(port); factory.ClientOptions.BaseAddress = new Uri($"http://127.0.0.1:{port}");
        Assert.ThrowsAny<Exception>(() => factory.CreateClient(factory.ClientOptions)); Assert.True(hostFault.Triggered && hostFault.SawSqlWrite);
        Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), x => x.Port == port);
    }
}
