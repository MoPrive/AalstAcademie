using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Hubs;
using AalstAcademie.Web.Models.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Na commit krijgen uitsluitend momenteel bevoegde beheerders een lege invalidatie.</summary>
public sealed class SignalRAccountApplicationNotifier(ApplicationDbContext context,
    IHubContext<AccountApplicationsHub> hub, DemoMode demoMode) : IAccountApplicationNotifier
{
    /// <summary>Selecteert actuele ontvangers en verstuurt uitsluitend een lege invalidatiemelding naar hun verbindingen.</summary>
    public async Task NotifyChangedAsync(CancellationToken cancellationToken = default)
    {
        // Een langdurige hubverbinding bewaart haar principal. Bepaal daarom ontvangers opnieuw uit de DB,
        // zonder statische beheerdersgroep. De snapshot wordt bij iedere fetch afzonderlijk geautoriseerd.
        var recipients = await (from user in context.Users.AsNoTracking()
            join link in context.UserRoles on user.Id equals link.UserId
            join role in context.Roles on link.RoleId equals role.Id
            where role.NormalizedName == "BEHEERDER" &&
                user.AccountApprovalStatus == AccountApprovalStatus.Approved && !user.IsBlocked &&
                (demoMode.IsEnabled || user.DemoSeedKey == null)
            select user.Id).Distinct().ToArrayAsync(cancellationToken);
        if (recipients.Length != 0)
            // Geen gegevens meegeven: een ontvanger moet voor de nieuwe snapshot opnieuw door autorisatie.
            // Een uitgesloten oudere verbinding wordt hier niet fysiek verbroken; zij krijgt deze melding niet.
            await hub.Clients.Users(recipients).SendAsync("AccountApplicationsChanged", cancellationToken);
    }
}
