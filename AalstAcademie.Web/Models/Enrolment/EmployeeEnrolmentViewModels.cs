using System.Globalization;
using AalstAcademie.Web.Models.Training;
using AalstAcademie.Web.Services.Training;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
namespace AalstAcademie.Web.Models.Enrolment;

/// <summary>Alleen eigen momentgegevens en behouden invoer; een fout ververst nooit de oorspronkelijke versietokens.</summary>
public sealed class EmployeeMomentViewModel
{
    public int MomentId { get; init; }
    public EmployeeMomentReadModel? Moment { get; init; }
    public EmployeeWaitlistInput Input { get; init; } = new();
    public bool Join { get; init; }
    public bool IsPost { get; init; }
    public bool IsConflict { get; init; }
}
public sealed class EmployeeWithdrawViewModel
{
    public OwnWaitlistRow Entry { get; init; } = null!;
    public EmployeeWithdrawInput Input { get; init; } = new();
    public bool IsConflict { get; init; }
}

/// <summary>HTTP-hulpmethoden voor uitsluitend deze formulieren; domeinregels blijven in de bestaande services.</summary>
internal static class EnrolmentHttpForms
{
    public static string Raw(ModelStateDictionary state, string key, object? fallback) =>
        state.TryGetValue(key, out var value) && value.AttemptedValue is not null ? value.AttemptedValue :
        Convert.ToString(fallback, CultureInfo.InvariantCulture) ?? "";
    public static Task CheckAsync(HttpRequest request, ModelStateDictionary state, string fields, CancellationToken ct) =>
        TrainingInputParsing.CheckFormAsync(request, state, fields, ct);
    public static IActionResult? Boundary(TrainingOperationStatus status) => status switch
    {
        TrainingOperationStatus.Forbidden => new StatusCodeResult(403),
        TrainingOperationStatus.NotFound => new NotFoundResult(),
        _ => null
    };
    public static void Feedback(Controller controller, TrainingOperationResult result)
    {
        controller.Response.StatusCode = result.Status switch
        { TrainingOperationStatus.Conflict => 409, TrainingOperationStatus.StorageUnavailable => 503, _ => 400 };
        foreach (var (key, messages) in result.Errors ?? new Dictionary<string, string[]>())
            foreach (var message in messages) controller.ModelState.AddModelError(key, message);
        var text = result.Status switch
        {
            TrainingOperationStatus.Conflict => "De gegevens, beschikbaarheid of voorwaarden zijn intussen gewijzigd. Lees het formulier bewust opnieuw vóór een nieuwe actie.",
            TrainingOperationStatus.StorageUnavailable => "De gegevens konden tijdelijk niet worden opgeslagen. Probeer later opnieuw.",
            _ => "Controleer de ingevulde velden."
        };
        controller.ModelState.AddModelError(string.Empty, text);
    }
    public static int Page(string? raw, string key, HttpRequest request, ModelStateDictionary state)
    {
        if (!request.Query.ContainsKey(key)) return 1;
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0) return page;
        state.AddModelError(key, "Kies een geldig positief paginanummer."); return 1;
    }
    public static void Query(HttpRequest request, ModelStateDictionary state, params string[] fields)
    {
        foreach (var (key, values) in request.Query)
            if (!fields.Contains(key, StringComparer.OrdinalIgnoreCase) || values.Count != 1)
                state.AddModelError(key, "Dit filter is ongeldig of meermaals opgegeven.");
    }
}
