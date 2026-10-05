using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Data;

/// <summary>Inspecteert raw accountkolommen vóór EF ze projecteert. Niet-lege oude namen hebben geen goedgekeurde conversie.</summary>
public static class AccountManagerMigrationPreflight
{
    public const string MigrationId = "20261004160000_UseAccountManagerReferences";
    private static readonly string[] Known = ["00000000000000_CreateIdentitySchema", "20261002124853_AddAccountFoundation",
        "20261002160213_AddAccountReview", "20261003125215_AddTrainingManagement", LegacyTrainingMigrationPreflight.RevisionMigration, MigrationId];
    public static async Task ValidateAsync(ApplicationDbContext context, CancellationToken ct = default)
    {
        if (!context.Database.IsSqlite()) throw new InvalidOperationException("De account-preflight vereist SQLite.");
        var connection = context.Database.GetDbConnection(); var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            var tables = await Strings(connection, "SELECT name FROM sqlite_master WHERE type='table'", ct);
            var history = tables.Contains("__EFMigrationsHistory") ? await Strings(connection,
                "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId", ct) : [];
            if (history.Length > Known.Length || !history.SequenceEqual(Known.Take(history.Length))) Fail();
            if (history.Length == 0)
            {
                if (tables.Any(x => !x.StartsWith("sqlite_", StringComparison.Ordinal) && x is not "__EFMigrationsHistory" and not "__EFMigrationsLock")) Fail();
                return;
            }
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[] { "Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "PasswordHash",
                "SecurityStamp", "ConcurrencyStamp", "PhoneNumber", "LockoutEnd" }) types[name] = "TEXT";
            foreach (var name in new[] { "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount" }) types[name] = "INTEGER";
            if (history.Length >= 2)
            {
                foreach (var name in new[] { "AccountApprovalStatus", "DepartmentId", "IsBlocked", "RequestedAccountType" }) types[name] = "INTEGER";
                foreach (var name in new[] { "FirstName", "LastName", "ManagerName", "ReviewedAt", "ReviewedById" }) types[name] = "TEXT";
            }
            if (history.Length >= 3) foreach (var name in new[] { "AccountRequestedAtUtc", "DemoSeedKey", "RefusalReason" }) types[name] = "TEXT";
            if (history.Length == 6) { types.Remove("ManagerName"); types["ManagerUserId"] = "TEXT"; }
            await Shape(connection, "AspNetUsers", types, ct);
            if (history.Length >= 2)
            {
                var departmentTypes = new Dictionary<string, string> { ["Id"] = "INTEGER", ["Name"] = "TEXT" };
                if (history.Length == 6) departmentTypes["ResponsibleUserId"] = "TEXT";
                await Shape(connection, "Departments", departmentTypes, ct);
            }
            if (history.Length is >= 2 and <= 5)
            {
                await using var command = connection.CreateCommand();
                // Geen trim: ook uitsluitend spaties en nultekens zijn oude informatie die we niet mogen weggooien.
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM AspNetUsers WHERE ManagerName IS NOT NULL AND ManagerName <> '')";
                if (Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0)
                    throw new InvalidOperationException("Oude leidinggevendenamen zijn aanwezig. Een afzonderlijk goedgekeurde conversie is vereist.");
            }
        }
        finally { if (opened) await connection.CloseAsync(); }
    }
    private static async Task Shape(DbConnection connection, string table, IReadOnlyDictionary<string, string> expected, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA table_info('{table}')";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var actual = new Dictionary<string, string>();
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(1); actual[name] = reader.GetString(2);
            if (name is "ManagerUserId" or "ResponsibleUserId" && reader.GetInt64(3) != 0) Fail();
        }
        if (actual.Count != expected.Count || expected.Any(x => !actual.TryGetValue(x.Key, out var type) || type != x.Value)) Fail();
    }
    private static async Task<string[]> Strings(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<string>();
        while (await reader.ReadAsync(ct)) rows.Add(reader.GetString(0)); return rows.ToArray();
    }
    private static void Fail() => throw new InvalidOperationException("Accountschema en bekende migratiegeschiedenis zijn niet consistent; opstart gestopt vóór wijzigingen.");
}
