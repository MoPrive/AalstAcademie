// Sprint 003: Begrenst het bezit en opruimen tot één GUID-map onder de expliciete tijdelijke testroot
// Controle op links en padgrenzen voorkomt opruimen buiten de fixture.
namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Bezit één GUID-map onder de expliciet gekozen tijdelijke testroot, buiten bron en buildoutput.</summary>
public sealed class TemporaryTestDirectory : IDisposable, IAsyncDisposable
{
    private bool disposed;
    public string DirectoryPath { get; }

    public TemporaryTestDirectory(string prefix = "revision02")
    {
        if (prefix.Length is < 1 or > 64 || prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Ongeldige testmapprefix.", nameof(prefix));
        var configured = Environment.GetEnvironmentVariable("AALSTACADEMIE_TEST_DATABASE_ROOT");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("Stel AALSTACADEMIE_TEST_DATABASE_ROOT expliciet in op een eigen tijdelijke testmap.");
        var root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar);
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
            root.Split(Path.DirectorySeparatorChar).Any(x => x is "bin" or "obj" or "verification-copy" or "migration-copy" or "ef-copy" or "demo-copy"))
            throw new InvalidOperationException("De testroot valt niet buiten bron/buildoutput onder Temp.");
        AssertNoReparseParents(root);
        DirectoryPath = Path.Combine(root, prefix + "-" + Guid.NewGuid().ToString("N"));
        AssertNoReparseParents(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
    }

    /// <summary>Alleen een gewone bestandsnaam mag een resource binnen deze eigen testmap aanwijzen.</summary>
    public string CreatePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || fileName is "." or "..")
            throw new ArgumentException("Gebruik een gewone testbestandsnaam.", nameof(fileName));
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, fileName));
        if (!string.Equals(Path.GetDirectoryName(path), DirectoryPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Testresource valt buiten eigen map.");
        return path;
    }

    /// <summary>Verwijdert uitsluitend eigen entries; directorylinks zelf verwijderen zonder hun target te volgen.</summary>
    public void Dispose()
    {
        if (disposed) return;
        AssertNoReparseParents(DirectoryPath);
        DeleteOwnedEntries(DirectoryPath);
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: false);
        disposed = true;
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    private void DeleteOwnedEntries(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var absolute = Path.GetFullPath(entry);
            if (!absolute.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cleanupgrens ongeldig.");
            var attributes = File.GetAttributes(absolute);
            if ((attributes & FileAttributes.Directory) == 0) File.Delete(absolute);
            else if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(absolute, recursive: false);
            else { DeleteOwnedEntries(absolute); Directory.Delete(absolute, recursive: false); }
        }
    }

    private static void AssertNoReparseParents(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparseouder in testpad geweigerd.");
    }
}
