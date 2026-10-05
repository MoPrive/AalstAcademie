// Sprint 003: Geeft de bootstrap een beperkte uitkomst van de accountbatch
// Een resultaat is geen wachtwoordcontainer en geen vrijgave vóór de aanbodseed.
namespace AalstAcademie.Web.Data;

/// <summary>Niet-geheim bootstrapresultaat; credentials blijven uitsluitend in het sessiesingleton.</summary>
public sealed class DemoSeedResult(bool isSeeded, int createdAccountCount)
{
    public bool IsSeeded { get; } = isSeeded;
    public int CreatedAccountCount { get; } = createdAccountCount;
    public static DemoSeedResult Empty { get; } = new(false, 0);
    public override string ToString() => $"DemoSeedResult: {CreatedAccountCount} nieuwe fictieve accounts";
}
