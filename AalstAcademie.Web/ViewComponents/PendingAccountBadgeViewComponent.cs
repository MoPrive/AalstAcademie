using AalstAcademie.Web.Security;
using AalstAcademie.Web.Services.Accounts;
using Microsoft.AspNetCore.Mvc;

namespace AalstAcademie.Web.ViewComponents;

/// <summary>Een actuele eerste teller wordt serverzijdig gerenderd, vóór de browserverbinding start.</summary>
public sealed class PendingAccountBadgeViewComponent(CurrentAccountAccessor currentAccount,
    AccountApplicationQueries queries) : ViewComponent
{
    /// <summary>Leest de actuele bevoegdheid en toont uitsluitend voor een goedgekeurde beheerder het aantal open aanvragen.</summary>
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var account = await currentAccount.GetAsync(HttpContext.RequestAborted);
        // Navigatie verbergen is geen toegangscontrole; de snapshotroute bewaakt dezelfde rechten zelfstandig.
        if (account?.IsApproved != true || !account.IsAdministrator) return Content(string.Empty);
        var snapshot = await queries.GetSnapshotAsync(cancellationToken: HttpContext.RequestAborted);
        return View(snapshot.TotalPending);
    }
}
