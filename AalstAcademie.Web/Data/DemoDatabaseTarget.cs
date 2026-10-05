// Sprint 003: Resolveert SQLite uitsluitend tegen ContentRoot en controleert het expliciete resetdoel, aliases, bestandstype en links vóór iedere reset
// Non-demo krijgt geen resetbevoegdheid.
using AalstAcademie.Web.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace AalstAcademie.Web.Data;

/// <summary>Eén expliciet SQLite-bestand, onafhankelijk van de werkdirectory van het proces.</summary>
public sealed class DemoDatabaseTarget
{
    private DemoDatabaseTarget(string databasePath, string connectionString) { DatabasePath = databasePath; ConnectionString = connectionString; }
    public string DatabasePath { get; }
    public string ConnectionString { get; }
    public string LeasePath => DatabasePath + ".application.lock";

    /// <summary>Valideert configuratie en bestaande padcomponenten; opent of maakt geen database.</summary>
    public static DemoDatabaseTarget From(IHostEnvironment environment, IConfiguration configuration, DemoMode mode)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(mode);
        var rawConnection = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(rawConnection)) throw InvalidTarget();
        SqliteConnectionStringBuilder connection;
        try { connection = new SqliteConnectionStringBuilder(rawConnection); }
        catch (ArgumentException exception) { throw InvalidTarget(exception); }
        if (connection.Mode is SqliteOpenMode.Memory or SqliteOpenMode.ReadOnly) throw InvalidTarget();
        var databasePath = CanonicalPath(connection.DataSource, environment.ContentRootPath);
        if (Directory.Exists(databasePath)) throw InvalidTarget();
        if (mode.IsEnabled && !PathEquals(databasePath, CanonicalPath(configuration["Demo:ResetTargetPath"], environment.ContentRootPath)))
            throw InvalidTarget();
        // Geen poolhandle mag na scope-disposal een providerreset of volgende eigen opstart hinderen.
        connection.DataSource = databasePath;
        connection.Pooling = false;
        connection.ForeignKeys = true;
        return new DemoDatabaseTarget(databasePath, connection.ToString());
    }

    /// <summary>Een reparsepoint in een ancestor mag het gevalideerde doel niet omleiden.</summary>
    internal static string CanonicalPath(string? raw, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw != raw.Trim() || raw.Contains('\0') || raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            raw == ":memory:" || raw.StartsWith("\\\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal)) throw InvalidTarget();
        try
        {
            if (string.IsNullOrWhiteSpace(contentRoot) || !Path.IsPathFullyQualified(contentRoot)) throw InvalidTarget();
            if (OperatingSystem.IsWindows())
            {
                // Win32 kan een eindpunt/spatie of devicenaam anders openen dan de lease-naam doet vermoeden.
                // Controle vóór GetFullPath voorkomt dat zo'n spelling eerst wordt genormaliseerd.
                ValidateWindowsComponents(raw, allowDotSegments: true);
                ValidateWindowsComponents(contentRoot, allowDotSegments: true);
            }
            var path = Path.GetFullPath(raw, Path.GetFullPath(contentRoot));
            if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) throw InvalidTarget();
            if (OperatingSystem.IsWindows()) ValidateWindowsComponents(path, allowDotSegments: false);
            for (string? candidate = path; candidate is not null; candidate = Path.GetDirectoryName(candidate))
            {
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) continue;
                var attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (candidate == path && (attributes & FileAttributes.ReadOnly) != 0)) throw InvalidTarget();
            }
            if (!Directory.Exists(Path.GetDirectoryName(path))) throw InvalidTarget();
            return path;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { throw InvalidTarget(exception); }
    }

    private static void ValidateWindowsComponents(string path, bool allowDotSegments)
    {
        var rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        foreach (var component in path[rootLength..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (allowDotSegments && (component is "." or "..")) continue;
            if (component.EndsWith('.') || component.EndsWith(' ') || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw InvalidTarget();
            // Ook CON.txt en COM1.db zijn gereserveerd; de extensie maakt er geen gewoon bestand van.
            var baseName = component.Split('.')[0].TrimEnd(' ');
            if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                baseName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || baseName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                (baseName.Length == 4 && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 (baseName[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
                throw InvalidTarget();
        }
    }

    // Windows-padvergelijking is hoofdletterongevoelig; elders blijven padletters betekenisvol. Beide paden zijn eerder gecanonicaliseerd.
    internal static bool PathEquals(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static InvalidOperationException InvalidTarget(Exception? inner = null) =>
        new("Het SQLite-doel is ontbrekend, ongeldig, omgeleid of verschilt van het expliciete resetdoel. Opstart geweigerd.", inner);
}
