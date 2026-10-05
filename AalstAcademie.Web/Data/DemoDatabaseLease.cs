// Sprint 003: Houdt de exclusieve applicatielease vast gedurende de hostlevensduur en controleert bestaande databasebestanden vóór reset
// Een tweede same-target host mag niet gaan schrijven.
namespace AalstAcademie.Web.Data;

/// <summary>Houdt één gewijzigde applicatie gedurende haar levensduur eigenaar van hetzelfde databankdoel.</summary>
public sealed class DemoDatabaseLease : IDisposable, IAsyncDisposable
{
    private readonly FileStream stream;
    private bool disposed;
    private DemoDatabaseLease(DemoDatabaseTarget target, FileStream stream) { Target = target; this.stream = stream; }
    public DemoDatabaseTarget Target { get; }

    /// <summary>Ook non-demo verkrijgt de lease: een tweede host mag een actieve host niet resetten.</summary>
    public static DemoDatabaseLease Acquire(DemoDatabaseTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _ = DemoDatabaseTarget.CanonicalPath(target.LeasePath, Path.GetDirectoryName(target.DatabasePath)!);
        try { return new(target, new FileStream(target.LeasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("De databank is reeds geleased of niet toegankelijk. Er werd niets gereset.", exception); }
    }

    /// <summary>Alle bestaande providerbestanden moeten eerst tegelijk exclusief toegankelijk blijken.</summary>
    public void ProbeResetFiles()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Demo-reset vereist de gecontroleerde Windows-bestandsprobe; hier wordt niet gereset.");
        var handles = new List<FileStream>();
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var path = Target.DatabasePath + suffix;
                _ = DemoDatabaseTarget.CanonicalPath(path, Path.GetDirectoryName(Target.DatabasePath)!);
                // Een directory op een SQLite-bestandsnaam is geen afwezig bestand: reset moet hier stoppen.
                if (Directory.Exists(path)) throw new InvalidOperationException("Een SQLite-bestandspad verwijst naar een directory. Er werd niets gereset.");
                if (File.Exists(path)) handles.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("Een SQLite-bestand is in gebruik of niet toegankelijk. Geen enkel databankbestand werd verwijderd.", exception); }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }

    // Laat alleen het leasehandle los; disposal verwijdert niet de database, providerbestanden of data.
    public void Dispose() { if (disposed) return; disposed = true; stream.Dispose(); }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    public override string ToString() => "Exclusieve applicatiedatabanklease";
}
