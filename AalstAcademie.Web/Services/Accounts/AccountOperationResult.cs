namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Laat de pagina/controller fouten gericht en zonder databasegegevens tonen.</summary>
// Conflict wordt een HTTP 409, Forbidden een 403 en NotFound een 404; validatiefouten
// kunnen bij het juiste formulierveld blijven zonder providerexceptions aan de gebruiker te tonen.
public enum AccountOperationStatus { Success, ValidationFailure, Conflict, Forbidden, NotFound, StorageUnavailable }

/// <summary>Succes betekent dat de volledige bewerking is gecommit, inclusief profiel of rollen.</summary>
public sealed record AccountOperationResult(AccountOperationStatus Status, string? UserId = null,
    IReadOnlyDictionary<string, string>? Errors = null)
{
    /// <summary>Alleen een definitieve commit mag de aanroeper als geslaagde bewerking behandelen.</summary>
    public bool Succeeded => Status == AccountOperationStatus.Success;
    /// <summary>Behoudt veldnamen als sleutels zodat de MVC/Identity-pagina fouten aan de juiste invoer koppelt.</summary>
    public static AccountOperationResult Invalid(IReadOnlyDictionary<string, string> errors) =>
        new(AccountOperationStatus.ValidationFailure, Errors: errors);
}
