using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Expliciet fictieve testvoorbereiding; geen publieke registratie of demowachtwoorden.</summary>
public static class TestAccountData
{
    /// <summary>Maakt de echte referentierollen en afdelingen klaar in uitsluitend de fixture-database.</summary>
    public static Task InitializeAsync(IServiceProvider services) =>
        services.GetRequiredService<DatabaseInitializer>().InitializeAsync();

    /// <summary>Bereidt een beheerder voor zonder publiek aanvraagtype; publieke registratie kan dit niet doen.</summary>
    public static async Task<ApplicationUser> CreateAdministratorAsync(IServiceProvider services) =>
        await CreateAsync(services, null, AccountApprovalStatus.Approved, roles: [RoleNames.Beheerder]);

    /// <summary>Laat tests ook strijdige rollen/statussen of ontbrekende profielen opbouwen om weigering te bewijzen.</summary>
    public static async Task<ApplicationUser> CreateAsync(IServiceProvider services, RequestedAccountType? type,
        AccountApprovalStatus approval = AccountApprovalStatus.Pending, bool blocked = false,
        IEnumerable<string>? roles = null, bool profile = true)
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"account-{Guid.NewGuid():N}@example.invalid";
        var external = type == RequestedAccountType.ExternalInstructor;
        var departmentId = !external && type is not null
            ? (int?)await context.Departments.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync() : null;
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(), UserName = email, Email = email, PhoneNumber = "0490000000",
            FirstName = external || type is null ? null : "Noor", LastName = external || type is null ? null : "Peeters",
            DepartmentId = departmentId, ManagerUserId = null,
            RequestedAccountType = type, AccountApprovalStatus = approval, IsBlocked = blocked,
            AccountRequestedAtUtc = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.AddDays(-1),
            SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString(), EmailConfirmed = false
        };
        // Deze service-fixtures hebben geen wachtwoord nodig; HTTP-loginfixtures gebruiken een andere helper.
        var creation = await manager.CreateAsync(user);
        if (!creation.Succeeded) throw new InvalidOperationException("Fictieve testgebruiker kon niet worden voorbereid.");
        // Rechtstreekse profielopslag kan ook geweigerde/legacy fixtures voorbereiden; testcode alleen.
        if (profile && type == RequestedAccountType.InternalInstructor)
            context.InternalInstructors.Add(new InternalInstructor { ApplicationUserId = user.Id });
        if (profile && external)
            context.ExternalInstructors.Add(new ExternalInstructor { ApplicationUserId = user.Id, OrganizationName = "Fictieve Test Academie" });
        await context.SaveChangesAsync();
        if (roles is not null)
        {
            var grant = await manager.AddToRolesAsync(user, roles);
            if (!grant.Succeeded) throw new InvalidOperationException("Fictieve testrollen konden niet worden voorbereid.");
        }
        return user;
    }
}
