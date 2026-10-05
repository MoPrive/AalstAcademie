using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Data;

/// <summary>Integratietests voor idempotente referentiedata, veilige standaardaccounts en DI.</summary>
public class DatabaseInitializerTests
{
    // Initialisatie moet zowel een verse database opbouwen als bestaande data behouden.
    [Fact]
    public async Task Initialization_twice_preserves_data_and_creates_one_set_of_reference_data()
    {
        await using var db = await SqliteTestDatabase.CreateAsync(migrate: false);
        var initializer = db.Services.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync();
        // Voeg na de eerste opstart eigen data toe die de tweede initialisatie moet respecteren.
        var user = await db.AddUserAsync(RequestedAccountType.Employee);
        var department = await db.Context.Departments.SingleAsync(x => x.Name == "ICT");
        var originalId = department.Id;
        db.Context.Departments.Add(new Department { Name = "Extra afdeling" });
        await db.Context.SaveChangesAsync();
        var manager = db.Services.GetRequiredService<UserManager<ApplicationUser>>();
        // Deze roltoekenning gebeurt alleen door de test: de initializer mag ze niet wegnemen.
        Assert.True((await manager.AddToRoleAsync(user, RoleNames.Medewerker)).Succeeded);
        await initializer.InitializeAsync();
        // Bewijs dat seeddata niet verdubbelt en de vooraf bestaande afdelingssleutel gelijk blijft.
        Assert.Equal(new[] { "Beheerder", "Lesgever", "Medewerker" }, await db.Context.Roles.OrderBy(x => x.Name).Select(x => x.Name).ToArrayAsync());
        foreach (var name in new[] { "ICT", "Personeel en Organisatie", "Onderhoud", "Administratie" })
            Assert.Equal(1, await db.Context.Departments.CountAsync(x => x.Name == name));
        Assert.Equal(originalId, (await db.Context.Departments.SingleAsync(x => x.Name == "ICT")).Id);
        Assert.True(await db.Context.Departments.AnyAsync(x => x.Name == "Extra afdeling"));
        Assert.Equal(new[] { RoleNames.Medewerker }, await manager.GetRolesAsync(user));
        Assert.Equal(AccountApprovalStatus.Pending, (await db.Context.Users.SingleAsync()).AccountApprovalStatus);
    }

    // Gebruik UserManager en herladen uit SQLite om het werkelijk opgeslagen resultaat te bewijzen.
    [Fact]
    public async Task Default_identity_user_stays_pending_unblocked_and_unprivileged_after_initialization()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var manager = db.Services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "new@example.invalid", Email = "new@example.invalid" };
        Assert.True((await manager.CreateAsync(user)).Succeeded);
        await db.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        // Voorkom dat EF alleen het al in geheugen aanwezige object teruggeeft.
        db.Context.ChangeTracker.Clear();
        var stored = await manager.FindByIdAsync(user.Id);
        Assert.NotNull(stored);
        Assert.Equal(AccountApprovalStatus.Pending, stored.AccountApprovalStatus);
        Assert.False(stored.IsBlocked);
        Assert.Null(stored.RequestedAccountType);
        Assert.False(stored.EmailConfirmed);
        Assert.Empty(await manager.GetRolesAsync(stored));
        Assert.Equal(1, await db.Context.Users.CountAsync());
    }

    // Een succesvolle build alleen bewijst niet dat DI de juiste generieke managers kan leveren.
    [Fact]
    public async Task Identity_graph_resolves_custom_managers_with_confirmation_policy_preserved()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var users = db.Services.GetRequiredService<UserManager<ApplicationUser>>();
        var signIn = db.Services.GetRequiredService<SignInManager<ApplicationUser>>();
        var roles = db.Services.GetRequiredService<RoleManager<IdentityRole>>();
        Assert.Same(users, signIn.UserManager);
        Assert.True(signIn.Options.SignIn.RequireConfirmedAccount);
        Assert.True(roles.SupportsQueryableRoles);
        Assert.Null(db.Services.GetService<UserManager<IdentityUser>>());
    }
}
