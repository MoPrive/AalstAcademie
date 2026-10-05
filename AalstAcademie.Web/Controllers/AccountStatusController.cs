using AalstAcademie.Web.Models.Accounts;
using AalstAcademie.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AalstAcademie.Web.Controllers;

/// <summary>Het beperkte eigen accountgebied blijft bereikbaar vóór goedkeuring.</summary>
[Authorize(Policy = AccountPolicies.AuthenticatedAccount)]
[Route("Account/Status")]
public sealed class AccountStatusController(CurrentAccountAccessor currentAccount) : Controller
{
    /// <summary>Toont de huidige goedkeuring en eventuele weigeringreden zonder gedeelde browsercache.</summary>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // Er is bewust geen invoer-ID: de caller kan alleen de eigen status bekijken.
        var account = await currentAccount.GetAsync(cancellationToken);
        if (account is null) return Challenge();
        return View(new AccountStatusViewModel(account.User.AccountApprovalStatus, account.User.ReviewedAt,
            account.User.RefusalReason, account.IsAdministrator));
    }
}
