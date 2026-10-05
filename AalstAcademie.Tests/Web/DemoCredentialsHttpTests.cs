// Sprint 003: Controleert de tijdelijke loginhint alleen na complete Development-demo-opbouw, zonder caching en zonder wachtwoorden in testuitvoer te publiceren.
using System.Net;
using System.Text.RegularExpressions;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Web;

/// <summary>Vijf HTTP-cases toetsen de bestaande tijdelijke loginhint zonder wachtwoord/HTML naar testoutput te schrijven.</summary>
public class DemoCredentialsHttpTests
{
    // DH1: volledige accountbootstrap publiceert zes fictieve hints; GET en iedere failedPOST zijn no-store.
    [Fact]
    public async Task Ready_demo_hint_is_labelled_and_uncached_on_GET_and_failed_POST()
    {
        await using var environment=new DemoStartupTestEnvironment();
        await using var factory=new DemoStartupWebApplicationFactory(environment);
        using var browser=factory.CreateIdentityClient();
        var credentials=factory.Services.GetRequiredService<DemoCredentials>();
        Assert.True(credentials.TryGetReadyHint(out var hint));Assert.True(credentials.IsReady);
        using var get=await browser.GetAsync("/Identity/Account/Login");Assert.Equal(HttpStatusCode.OK,get.StatusCode);
        Assert.True(get.Headers.CacheControl?.NoStore==true);CheckHint(await get.Content.ReadAsStringAsync(),hint!);
        // Verschillende mislukte gewone Identity-invoer; geen verborgen/autofill-wachtwoord.
        foreach(var empty in new[]{false,true})
        {
            using var failed=await browser.PostFormAsync("/Identity/Account/Login",new Dictionary<string,string>{
                ["Input.Email"]=empty?"":"onbekend@example.invalid",["Input.Password"]=empty?"":AccountWebApplicationFactory.NewPassword(),["Input.RememberMe"]="false"});
            Assert.Equal(HttpStatusCode.OK,failed.StatusCode);Assert.True(failed.Headers.CacheControl?.NoStore==true);
            CheckHint(await failed.Content.ReadAsStringAsync(),hint!);
        }
        await factory.WithServicesAsync(async services=>{
            var manager=services.GetRequiredService<UserManager<ApplicationUser>>();
            foreach(var account in DemoAccountCatalog.All)
            {
                var user=await manager.FindByIdAsync(account.Id);Assert.NotNull(user);
                // Alleen een boolean verlaat deze check; plaintext/hash is nooit een assertargument.
                Assert.True(await manager.CheckPasswordAsync(user!,hint!.Password));
            }
        });
    }

    // DH2: buiten effectieve/volledige demo verschijnt geen widget en lezen genereert geen wachtwoord.
    [Theory] [InlineData("Development-off")] [InlineData("Production-off")] [InlineData("suppressed")]
    public async Task Hint_absent_outside_completed_demo(string variant)
    {
        await using var environment=new DemoStartupTestEnvironment {EnvironmentName=variant=="Production-off"?"Production":"Development"};
        await using var factory=new DemoStartupWebApplicationFactory(environment,demoEnabled:variant=="suppressed",suppressSeed:variant=="suppressed");
        using var browser=factory.CreateIdentityClient();var credentials=factory.Services.GetRequiredService<DemoCredentials>();
        Assert.False(credentials.IsReady);Assert.False(credentials.TryGetReadyHint(out _));
        using var response=await browser.GetAsync("/Identity/Account/Login");Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var html=await response.Content.ReadAsStringAsync();Assert.False(html.Contains("Aanmeldgegevens voor de fictieve demosessie",StringComparison.Ordinal));
        Assert.False(html.Contains("Wachtwoord voor deze sessie:",StringComparison.Ordinal));Assert.False(credentials.TryGetReadyHint(out _));
    }

    // DH3: de hint is geen alternatieve authroute; Approved en Pending doorlopen normale Identity-login en eigen statusrouting.
    [Fact]
    public async Task Approved_and_pending_accounts_use_normal_Identity_login()
    {
        await using var environment=new DemoStartupTestEnvironment();await using var factory=new DemoStartupWebApplicationFactory(environment);
        using(var initial=factory.CreateIdentityClient()){} // Start precies één host met zijn eigen database.
        Assert.True(factory.Services.GetRequiredService<DemoCredentials>().TryGetReadyHint(out var hint));
        foreach(var approval in new[]{AccountApprovalStatus.Approved,AccountApprovalStatus.Pending})
        {
            var account=DemoAccountCatalog.All.First(x=>x.Approval==approval);
            using var browser=factory.CreateIdentityClient();
            using var login=await browser.LoginAsync(new TestIdentity(account.Id,account.Email,hint!.Password,null));
            Assert.Equal(HttpStatusCode.Redirect,login.StatusCode);
            if(approval==AccountApprovalStatus.Pending)Assert.Equal("/Account/Status",login.Headers.Location?.OriginalString);
            using var home=await browser.GetAsync("/");
            Assert.Equal(approval==AccountApprovalStatus.Approved?HttpStatusCode.OK:HttpStatusCode.Redirect,home.StatusCode);
            await factory.WithServicesAsync(async services=>{
                var user=await services.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(account.Id);
                Assert.Equal(approval,user!.AccountApprovalStatus);
            });
        }
    }
    /// <summary>Alle HTML/secretcontroles geven uitsluitend booleans door, ook bij een falende assert.</summary>
    private static void CheckHint(string html,DemoCredentialHint hint)
    {
        Assert.True(html.Contains("<details",StringComparison.Ordinal));Assert.True(html.Contains("<summary>",StringComparison.Ordinal));
        Assert.True(html.Contains("Aanmeldgegevens voor de fictieve demosessie",StringComparison.Ordinal));
        Assert.True(html.Contains("Wachtwoord voor deze sessie:",StringComparison.Ordinal));
        Assert.True(WebUtility.HtmlDecode(html).Contains(hint.Password,StringComparison.Ordinal));
        Assert.Equal(6,hint.Emails.Count);
        foreach(var email in hint.Emails)Assert.True(html.Contains(email,StringComparison.Ordinal));
        var input=Regex.Match(html,"(?is)<input\\b(?=[^>]*\\bname=[\"']Input.Password[\"'])[^>]*>");
        Assert.True(input.Success);var value=Regex.Match(input.Value,"(?is)\\bvalue=[\"']([^\"']*)[\"']");
        Assert.True(!value.Success||value.Groups[1].Value.Length==0);
        // De lokale boolean voorkomt dat een regex-/HTML-assert bij failure de tijdelijke hint uitschrijft.
        var hasAutofillSecret=Regex.IsMatch(html,"(?is)<input\\b[^>]*\\bvalue=[\"']"+Regex.Escape(WebUtility.HtmlEncode(hint.Password))+"[\"']");
        Assert.False(hasAutofillSecret);
    }
}

