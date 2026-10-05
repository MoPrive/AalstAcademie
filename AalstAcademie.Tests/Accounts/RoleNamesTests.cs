using AalstAcademie.Web.Models.Identity;

namespace AalstAcademie.Tests.Accounts;

/// <summary>Vijf unit-testgevallen bewaken de gewenste rolmapping en sluiten Beheerder uit.</summary>
public class RoleNamesTests
{
    [Theory]
    [InlineData(1, new[] { "Medewerker" })]
    [InlineData(2, new[] { "Medewerker", "Lesgever" })]
    [InlineData(3, new[] { "Lesgever" })]
    [InlineData(0, new string[0])]
    [InlineData(99, new string[0])]
    public void Maps_only_eligible_non_administrator_roles(int type, string[] expected)
    {
        // 0 stelt null voor; 99 simuleert een niet-gedefinieerde enumwaarde.
        var roles = RoleNames.ForApprovedAccountType(type == 0 ? null : (RequestedAccountType)type);
        Assert.Equal(expected, roles);
        // Ook wanneer de verwachte combinatie klopt, mag de mapping nooit admin opleveren.
        Assert.DoesNotContain(RoleNames.Beheerder, roles);
    }
}
