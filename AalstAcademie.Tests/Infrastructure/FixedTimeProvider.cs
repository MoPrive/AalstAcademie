namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Vaste UTC-klok maakt aanvraag- en reviewmetadata reproduceerbaar zonder sleeps.</summary>
public sealed class FixedTimeProvider(DateTimeOffset? utcNow = null) : TimeProvider
{
    // Een test kan tijd bewust vooruitzetten; de productiecode blijft alleen TimeProvider aanspreken.
    public DateTimeOffset UtcNow { get; set; } = utcNow ?? new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    /// <summary>Geeft dezelfde UTC-tijd voor aanvraag, beoordeling en de daaropvolgende assertions.</summary>
    public override DateTimeOffset GetUtcNow() => UtcNow;
}
