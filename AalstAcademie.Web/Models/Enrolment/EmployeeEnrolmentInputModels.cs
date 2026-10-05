using System.ComponentModel.DataAnnotations;

namespace AalstAcademie.Web.Models.Enrolment;

/// <summary>Alleen de gelezen voorwaardenversies en eigen motivatie zijn invoer; actor/status/tijd zijn servergegevens.</summary>
public record EmployeeRequestInput
{
    [Required(ErrorMessage = "Lees het moment opnieuw om de opleidingsversie te verkrijgen.")]
    public Guid? ExpectedTrainingVersion { get; init; }
    [Required(ErrorMessage = "Lees het moment opnieuw om de momentversie te verkrijgen.")]
    public Guid? ExpectedMomentVersion { get; init; }
    public string? Motivation { get; init; }
}

/// <summary>Een volle aanvraag wordt nooit automatisch een buffer; de gebruiker geeft afzonderlijk toestemming.</summary>
public sealed record EmployeeWaitlistInput : EmployeeRequestInput
{
    public bool Consent { get; init; }
}

/// <summary>De route noemt één concrete buffer; de oorspronkelijke versie voorkomt intrekken van een latere aansluiting.</summary>
public sealed record EmployeeWithdrawInput
{
    [Required(ErrorMessage = "Lees de buffer opnieuw om de oorspronkelijke versie te verkrijgen.")]
    public Guid? ExpectedVersion { get; init; }
}

public sealed record MotivationValidation(string? Value, IReadOnlyDictionary<string, string[]> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}
