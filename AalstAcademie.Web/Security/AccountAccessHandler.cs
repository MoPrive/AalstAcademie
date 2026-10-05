using Microsoft.AspNetCore.Authorization;

namespace AalstAcademie.Web.Security;

/// <summary>Past de named policies toe op de actuele accounttoestand en opgeslagen beheerdersrol.</summary>
public sealed class AccountAccessHandler(CurrentAccountAccessor accounts)
    : AuthorizationHandler<AccountAccessRequirement>
{
    /// <summary>Laat een vereiste alleen slagen wanneer de huidige opgeslagen accountlezing alle gevraagde rechten bezit.</summary>
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AccountAccessRequirement requirement)
    {
        var account = await accounts.GetAsync(context.User);
        if (account is null)
            return;
        // Een beheerdersvereiste veronderstelt eveneens goedkeuring, ook als een
        // toekomstige aanroeper die combinatie per ongeluk onvolledig opgeeft.
        if ((requirement.RequireApproval || requirement.RequireAdministrator) && !account.IsApproved)
            return;
        if (requirement.RequireAdministrator && !account.IsAdministrator)
            return;
        // Een ontbrekende Succeed betekent dat autorisatie faalt. De handler voert zelf geen redirect of paginaactie uit.
        context.Succeed(requirement);
    }
}
