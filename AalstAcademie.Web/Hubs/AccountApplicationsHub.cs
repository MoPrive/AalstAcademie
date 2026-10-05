using AalstAcademie.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AalstAcademie.Web.Hubs;

/// <summary>Alleen actuele goedgekeurde beheerders verbinden; clients kunnen geen gegevens of groepen wijzigen.</summary>
/// <remarks>
/// De hub heeft geen publiek oproepbare businessmethoden. De server verstuurt alleen
/// AccountApplicationsChanged als lege melding; de browser haalt de gegevens daarna via
/// het afzonderlijk geautoriseerde snapshotendpoint op.
/// </remarks>
[Authorize(Policy = AccountPolicies.ApprovedAdministrator)]
public sealed class AccountApplicationsHub : Hub;
