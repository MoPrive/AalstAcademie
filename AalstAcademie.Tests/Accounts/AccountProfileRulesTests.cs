using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;

namespace AalstAcademie.Tests.Accounts;

/// <summary>Unit-tests voor de pure regels: geldige vormen en veldgerichte fouten zonder database.</summary>
public class AccountProfileRulesTests
{
    // Elke InlineData-rij is een afzonderlijk uitgevoerd testgeval (hier 24).
    // Ongeldige gevallen moeten de veldsleutel opleveren die het formulier kan tonen.
    [Theory]
    [InlineData(1, "complete", null)]
    [InlineData(2, "complete", null)]
    [InlineData(3, "complete", null)]
    [InlineData(3, "vat", null)]
    [InlineData(1, "first", "FirstName")]
    [InlineData(1, "last", "LastName")]
    [InlineData(1, "department", "DepartmentId")]
    [InlineData(1, "manager", null)]
    [InlineData(1, "organization", "OrganizationName")]
    [InlineData(1, "vat", "VatNumber")]
    [InlineData(2, "first", "FirstName")]
    [InlineData(2, "last", "LastName")]
    [InlineData(2, "department", "DepartmentId")]
    [InlineData(2, "organization", "OrganizationName")]
    [InlineData(2, "vat", "VatNumber")]
    [InlineData(3, "organization", "OrganizationName")]
    [InlineData(3, "department", "DepartmentId")]
    [InlineData(3, "first", "FirstName")]
    [InlineData(3, "last", "LastName")]
    [InlineData(3, "manager", "ManagerUserId")]
    [InlineData(0, "complete", "RequestedAccountType")]
    [InlineData(99, "complete", "RequestedAccountType")]
    [InlineData(1, "longFirst", "FirstName")]
    [InlineData(3, "longVat", "VatNumber")]
    public void Validates_complete_account_shape(int type, string variation, string? expectedField)
    {
        // Arrange: start met een passend volledig profiel en wijzig daarna één gegeven.
        var external = type == 3;
        var user = new ApplicationUser
        {
            RequestedAccountType = type == 0 ? null : (RequestedAccountType)type,
            FirstName = external ? null : "Noor", LastName = external ? null : "Peeters",
            DepartmentId = external ? null : 1, ManagerUserId = null
        };
        string? organization = external ? "Demo Opleidingen" : null;
        string? vat = null;
        // Dezelfde variatie krijgt per type een andere betekenis: bij intern ontbreekt
        // bijvoorbeeld de naam, bij extern wordt juist een verboden persoonlijke naam ingevuld.
        switch (variation)
        {
            case "first": user.FirstName = external ? "Noor" : "  "; break;
            case "last": user.LastName = external ? "Peeters" : null; break;
            case "department": user.DepartmentId = external ? 1 : null; break;
            case "manager": user.ManagerUserId = external ? "Robin" : " "; break;
            case "organization": organization = external ? "  " : "Demo"; break;
            case "vat": vat = "BE0123456789"; break;
            case "longFirst": user.FirstName = new string('a', 101); break;
            case "longVat": vat = new string('a', 33); break;
        }

        // Act: één pure validatieaanroep, zonder Identity, database of rollen.
        var errors = AccountProfileRules.Validate(user, organization, vat);
        // Assert: geldig geeft geen fouten; de gewijzigde ongeldige vorm precies één veldfout.
        if (expectedField is null) Assert.Empty(errors);
        else
        {
            Assert.Single(errors);
            Assert.True(errors.ContainsKey(expectedField));
            Assert.False(string.IsNullOrWhiteSpace(errors[expectedField]));
        }
        // Gelijke invoer geeft gelijke fouten: de validatie is deterministisch.
        Assert.Equal(errors, AccountProfileRules.Validate(user, organization, vat));
    }
}
