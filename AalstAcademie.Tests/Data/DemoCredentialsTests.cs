// Sprint 003: Bewijst dat lezen geen wachtwoord genereert en dat een onvolledige accountbatch geen loginhint publiceert
// Generatie gebruikt een cryptografische generator.
using AalstAcademie.Web.Data;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Data;

/// <summary>Een nog niet voltooide demo-opstart publiceert geen aanmeldgegevens en veroorzaakt bij lezen geen generatie.</summary>
public class DemoCredentialsTests
{
    [Fact]
    public void Fresh_session_has_no_ready_hint_and_safe_object_text()
    {
        var credentials = new DemoCredentials();

        Assert.False(credentials.IsReady);
        Assert.False(credentials.TryGetReadyHint(out var hint));
        Assert.Null(hint);
        // De generieke objectweergave blijft bruikbaar zonder een e-mailadres of wachtwoordwaarde.
        Assert.False(credentials.ToString().Contains("@", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => credentials.PublishReady(new DemoSeedResult(true, 6)));
        Assert.False(credentials.IsReady);
        Assert.False(credentials.TryGetReadyHint(out _));
    }

    [Fact]
    public async Task Complete_bootstrap_has_one_shared_twelve_character_password_with_normal_identity_hashes_and_session_local_publication()
    {
        using var environment = new DemoStartupTestEnvironment();
        await using var provider = environment.CreateServices();
        await using var scope = provider.CreateAsyncScope();
        _ = scope.ServiceProvider.GetRequiredService<TrainingSchedule>();
        await scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>().InitializeAsync();
        var credentials = provider.GetRequiredService<DemoCredentials>();
        Assert.True(credentials.TryGetReadyHint(out var hint));
        Assert.True(hint is not null);
        // Alleen booleans worden geassert: een falende test mag geen passwordwaarde afdrukken.
        Assert.True(hint!.Password.Length == 12);
        Assert.True(hint.Password.Any(char.IsUpper) && hint.Password.Any(char.IsLower) && hint.Password.Any(char.IsDigit) && hint.Password.Any(c => !char.IsLetterOrDigit(c)));
        Assert.Equal(6, hint.Emails.Count);
        Assert.True(hint.Emails.All(email => email.EndsWith("@example.invalid", StringComparison.Ordinal)));
        Assert.False(credentials.ToString().Contains(hint.Password, StringComparison.Ordinal));
        Assert.False(hint.ToString().Contains(hint.Password, StringComparison.Ordinal));
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var users = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().ToArrayAsync();
        Assert.Equal(6, users.Length);
        foreach (var user in users)
        {
            Assert.True(await manager.CheckPasswordAsync(user, hint.Password));
            Assert.True(!string.IsNullOrEmpty(user.PasswordHash) && !string.IsNullOrEmpty(user.SecurityStamp) && !string.IsNullOrEmpty(user.ConcurrencyStamp));
        }
        Assert.True(credentials.TryGetReadyHint(out var sameHint) && ReferenceEquals(hint, sameHint));
        var differentSession = new DemoCredentials();
        Assert.False(differentSession.IsReady);
        Assert.False(differentSession.TryGetReadyHint(out _));
        credentials.Clear();
        Assert.False(credentials.TryGetReadyHint(out _));
    }
}
