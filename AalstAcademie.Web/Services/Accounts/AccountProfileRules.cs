using AalstAcademie.Web.Models.Identity;

namespace AalstAcademie.Web.Services.Accounts;

/// <summary>
/// Controleert uitsluitend de gegevensvorm van een volledig accountprofiel.
/// Deze pure regels slaan niets op, keuren niets goed en kennen geen rollen toe.
/// </summary>
public static class AccountProfileRules
{
    /// <summary>
    /// Retourneert fouten per veldnaam, zodat de toekomstige registratiepagina ze kan tonen.
    /// Organisatie/btw horen bij het externe profiel en staan daarom niet op ApplicationUser.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Validate(
        ApplicationUser user, string? organizationName = null, string? vatNumber = null)
    {
        // Ordinal vergelijkt veldsleutels exact en cultuuronafhankelijk.
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        // Gemeenschappelijke controle voor verplichte tekst en maximale lengte.
        void Required(string field, string? value, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value)) errors[field] = "Dit veld is verplicht.";
            else if (value.Length > maximum) errors[field] = $"Gebruik maximaal {maximum} tekens.";
        }
        // Voorkomt dat een profiel gegevens bevat die bij een ander accounttype horen.
        void Absent(string field, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) errors[field] = "Dit veld hoort niet bij dit accounttype.";
        }

        // Het aangevraagde type bepaalt de gegevensvorm, niet de verleende rechten.
        switch (user.RequestedAccountType)
        {
            case RequestedAccountType.Employee:
            case RequestedAccountType.InternalInstructor:
                // Beide interne typen vereisen persoonlijke namen en een afdelingssleutel.
                // Het bestaan van die afdeling wordt door de database-FK gecontroleerd.
                Required(nameof(user.FirstName), user.FirstName, 100);
                Required(nameof(user.LastName), user.LastName, 100);
                if (user.DepartmentId is null or <= 0) errors[nameof(user.DepartmentId)] = "Kies een afdeling.";
                // Sprint 004: beide interne typen mogen Geen kiezen. De registratiedienst
                // controleert een nieuwe FK-keuze onder de write-lock; review herkeurt die keuze niet.
                // Organisatiegegevens mogen geen onderdeel van een intern profiel zijn.
                Absent("OrganizationName", organizationName);
                Absent("VatNumber", vatNumber);
                break;
            case RequestedAccountType.ExternalInstructor:
                // Een externe organisatie heeft geen persoonlijke namen, interne afdeling
                // of leidinggevende op het account; btw blijft optioneel.
                Required("OrganizationName", organizationName, 200);
                if (vatNumber?.Length > 32) errors["VatNumber"] = "Gebruik maximaal 32 tekens.";
                Absent(nameof(user.FirstName), user.FirstName);
                Absent(nameof(user.LastName), user.LastName);
                Absent(nameof(user.ManagerUserId), user.ManagerUserId);
                if (user.DepartmentId is not null) errors[nameof(user.DepartmentId)] = "Dit veld hoort niet bij dit accounttype.";
                break;
            default:
                // Een legacy/onvolledig account mag bestaan, maar is geen volledig profiel.
                errors[nameof(user.RequestedAccountType)] = "Kies een geldig accounttype.";
                break;
        }
        // Leeg betekent: deze gegevensvorm is geldig. Het betekent geen accountgoedkeuring.
        return errors;
    }
}
