// Sprint 003: Inspecteert de echte oude opleidingstabel vóór schemawijziging
// Zonder goedgekeurde conversieregel blokkeert iedere oude opleidingsrij de upgrade.
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Data;

/// <summary>Ondersteunt alleen bekende fysieke schemas; aanwezige oude opleidingen behoeven een aparte conversiebeslissing.</summary>
public static class LegacyTrainingMigrationPreflight
{
    public const string RevisionMigration = "20261004000000_SplitTrainingDefinitionsAndMoments";
    private static readonly string[] KnownMigrations =
    ["00000000000000_CreateIdentitySchema", "20261002124853_AddAccountFoundation",
     "20261002160213_AddAccountReview", "20261003125215_AddTrainingManagement", RevisionMigration, AccountManagerMigrationPreflight.MigrationId];

    /// <summary>Raw schema/history-reads komen vóór migratie en referentiewrites, met behoud van de verbindingslevensduur.</summary>
    public static async Task ValidateAsync(ApplicationDbContext context, CancellationToken cancellationToken = default)
    {
        if (!context.Database.IsSqlite()) throw new InvalidOperationException("De schema-preflight vereist SQLite.");
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            var tables = await ReadStringsAsync(connection, "SELECT name FROM sqlite_master WHERE type='table'", cancellationToken);
            string[] history = tables.Contains("__EFMigrationsHistory", StringComparer.Ordinal)
                ? await ReadStringsAsync(connection, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId", cancellationToken) : [];
            if (history.Length > KnownMigrations.Length || !history.SequenceEqual(KnownMigrations.Take(history.Length)))
                throw new InvalidOperationException("Onbekende migratiegeschiedenis; opstart gestopt vóór schemawijzigingen.");
            if (history.Length > 0)
            {
                // Een gedeeltelijk, onbekend of handmatig afwijkend schema is geen bewezen lege legacydatabase.
                string[] identity = ["AspNetUsers", "AspNetRoles", "AspNetUserRoles", "AspNetUserClaims",
                    "AspNetRoleClaims", "AspNetUserLogins", "AspNetUserTokens"];
                var expected = identity.ToHashSet(StringComparer.Ordinal);
                if (history.Length >= 2) expected.UnionWith(["Departments", "InternalInstructors", "ExternalInstructors"]);
                if (history.Length >= 4) expected.UnionWith(["Categories", "Trainings", "TrainingDepartments"]);
                if (history.Length >= 5) expected.UnionWith(["Locations", "TrainingMoments", "Registrations", "WaitlistEntries"]);
                var domain = tables.Where(x => !x.StartsWith("sqlite_", StringComparison.Ordinal) &&
                    x is not "__EFMigrationsHistory" and not "__EFMigrationsLock").ToHashSet(StringComparer.Ordinal);
                if (!expected.SetEquals(domain))
                    throw new InvalidOperationException("Schema en migratiegeschiedenis zijn niet consistent.");
            }
            var hasTrainings = tables.Contains("Trainings", StringComparer.Ordinal);
            if (!hasTrainings)
            {
                if (history.Length >= 4 || history.Length == 0 && tables.Any(x => !x.StartsWith("sqlite_", StringComparison.Ordinal) && x is not "__EFMigrationsHistory" and not "__EFMigrationsLock"))
                    throw new InvalidOperationException("Schema en migratiegeschiedenis zijn niet consistent.");
                return;
            }
            var columns = await ReadStringsAsync(connection, "SELECT name FROM pragma_table_info('Trainings')", cancellationToken);
            if (history.Length >= 5)
            {
                if (!columns.Contains("InstructorUserId", StringComparer.Ordinal) || columns.Contains("IsArchived", StringComparer.Ordinal) || columns.Contains("Date", StringComparer.Ordinal))
                    throw new InvalidOperationException("Het herziene opleidingsschema is niet consistent.");
                return; // Echte nieuwe definities bij non-demoherstart zijn bewust toegestaan.
            }
            if (history.Length != 4 || !columns.Contains("IsArchived", StringComparer.Ordinal) ||
                !columns.Contains("InternalInstructorId", StringComparer.Ordinal) || !columns.Contains("ExternalInstructorId", StringComparer.Ordinal) || columns.Contains("InstructorUserId", StringComparer.Ordinal))
                throw new InvalidOperationException("Onbekend oud opleidingsschema; geen conversie verzinnen.");
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT EXISTS(SELECT 1 FROM Trainings)";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)) != 0)
                throw new InvalidOperationException("Oude opleidingen zijn aanwezig. Een afzonderlijk goedgekeurde conversie is vereist.");
        }
        finally { if (openedHere) await connection.CloseAsync(); }
    }

    private static async Task<string[]> ReadStringsAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
        return values.ToArray();
    }
}
