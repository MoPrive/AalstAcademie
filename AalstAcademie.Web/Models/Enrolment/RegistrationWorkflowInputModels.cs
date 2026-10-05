namespace AalstAcademie.Web.Models.Enrolment;

/// <summary>Alleen de twee oorspronkelijk gelezen versies zijn invoer; actor, status en tijd blijven servergegevens.</summary>
public record RegistrationConfirmInput
{
    public Guid? ExpectedRegistrationVersion { get; init; }
    public Guid? ExpectedMomentVersion { get; init; }
}
public sealed record RegistrationRefuseInput : RegistrationConfirmInput
{
    public string? DecisionReason { get; init; }
}
public sealed record RegistrationCancelInput : RegistrationConfirmInput;
public sealed record TrainingMomentCancelInput
{
    public Guid? ExpectedMomentVersion { get; init; }
}

/// <summary>Ook directe serviceaanroepen moeten geldige tokens en een begrensde optionele weigerreden leveren.</summary>
internal static class RegistrationWorkflowValidation
{
    internal static Dictionary<string, string[]> Versions(RegistrationConfirmInput input)
    {
        var errors = MomentVersion(input.ExpectedMomentVersion);
        if (input.ExpectedRegistrationVersion is null || input.ExpectedRegistrationVersion == Guid.Empty)
            errors["ExpectedRegistrationVersion"] = ["Lees de oorspronkelijke registratieversie opnieuw."];
        return errors;
    }
    internal static Dictionary<string, string[]> MomentVersion(Guid? version) =>
        version is null || version == Guid.Empty
            ? new() { ["ExpectedMomentVersion"] = ["Lees de oorspronkelijke momentversie opnieuw."] } : new();
    internal static string? Reason(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    internal static bool ValidReason(string? value) => value is null || value.Length <= 1000 && !value.Contains('\0');
}
