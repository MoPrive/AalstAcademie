// Sprint 003: Controleert de volgorde doelcontrole, lease, reset, migratie, referenties, accounts en vrijgave
// Een late opslagfout mag geen succesvolle aanmeldhint opleveren.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Services.Training;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Data;

/// <summary>De echte provider, migrator, referenties en accountbatch moeten slagen vóór credentialpublicatie.</summary>
public class DemoDatabaseBootstrapTests
{
    [Fact]
    public async Task Demo_resets_old_file_then_builds_schema_references_accounts_and_only_then_publishes_ready()
    {
        using var environment = new DemoStartupTestEnvironment();
        await using (var old = new SqliteConnection(environment.ConnectionString))
        {
            await old.OpenAsync();
            await using var command = old.CreateCommand();
            command.CommandText = "CREATE TABLE OldSentinel(Value INTEGER); INSERT INTO OldSentinel VALUES(43);";
            await command.ExecuteNonQueryAsync();
        }
        await using var provider = environment.CreateServices();
        await using var scope = provider.CreateAsyncScope();
        var credentials = provider.GetRequiredService<DemoCredentials>();
        Assert.False(credentials.IsReady);
        // De zone wordt vóór de lifetimelease gevalideerd, zoals in de echte Program-opstart.
        _ = scope.ServiceProvider.GetRequiredService<TrainingSchedule>();
        await scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>().InitializeAsync();
        Assert.True(credentials.IsReady);
        var target = provider.GetRequiredService<DemoDatabaseTarget>();
        Assert.Throws<InvalidOperationException>(() => DemoDatabaseLease.Acquire(target));
        // Onafhankelijke verbinding leest werkelijk duurzame rijen; geen EF-trackingbewijs.
        await using var read = new SqliteConnection(environment.ConnectionString);
        await read.OpenAsync();
        Assert.Equal(0L, await ScalarAsync(read, "SELECT COUNT(*) FROM sqlite_master WHERE name='OldSentinel'"));
        Assert.Equal(6L, await ScalarAsync(read, "SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.Equal(3L, await ScalarAsync(read, "SELECT COUNT(*) FROM AspNetRoles"));
        Assert.Equal(4L, await ScalarAsync(read, "SELECT COUNT(*) FROM Departments"));
        Assert.Equal(6L, await ScalarAsync(read, "SELECT COUNT(*) FROM AspNetUsers"));
    }

    [Fact]
    public async Task Non_demo_preserves_existing_data_and_does_not_seed_or_publish_credentials()
    {
        using var environment = new DemoStartupTestEnvironment();
        string before;
        await using (var first = environment.CreateServices(demoEnabled: false))
        {
            await using var scope = first.CreateAsyncScope();
            _ = scope.ServiceProvider.GetRequiredService<TrainingSchedule>();
            await scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>().InitializeAsync();
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlRawAsync(
                "INSERT INTO Departments(Name) VALUES('Bewaarde fictieve afdeling');");
            Assert.False(first.GetRequiredService<DemoCredentials>().IsReady);
            before = await RawDataDigestAsync(environment.ConnectionString);
        }
        await using (var second = environment.CreateServices(demoEnabled: false))
        {
            await using var scope = second.CreateAsyncScope();
            _ = scope.ServiceProvider.GetRequiredService<TrainingSchedule>();
            await scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>().InitializeAsync();
            Assert.False(second.GetRequiredService<DemoCredentials>().IsReady);
        }
        await using var read = new SqliteConnection(environment.ConnectionString);
        await read.OpenAsync();
        Assert.Equal(before, await RawDataDigestAsync(environment.ConnectionString));
        Assert.Equal(1L, await ScalarAsync(read, "SELECT COUNT(*) FROM Departments WHERE Name='Bewaarde fictieve afdeling'"));
        Assert.Equal(0L, await ScalarAsync(read, "SELECT COUNT(*) FROM AspNetUsers"));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("reference")]
    [InlineData("account")]
    public async Task Real_late_sql_failure_aborts_bootstrap_and_never_publishes_hint(string point)
    {
        using var environment = new DemoStartupTestEnvironment();
        var failure = new DemoStartupFailureInterceptor();
        failure.Arm(point);
        await using var provider = environment.CreateServices(interceptor: failure);
        await using var scope = provider.CreateAsyncScope();
        _ = scope.ServiceProvider.GetRequiredService<TrainingSchedule>();
        await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>().InitializeAsync());
        Assert.True(failure.Triggered);
        Assert.True(failure.SawSqlWrite);
        var credentials = provider.GetRequiredService<DemoCredentials>();
        Assert.False(credentials.IsReady);
        Assert.False(credentials.TryGetReadyHint(out _));
        await using var read = new SqliteConnection(environment.ConnectionString);
        await read.OpenAsync();
        if (await ScalarAsync(read, "SELECT COUNT(*) FROM sqlite_master WHERE name='AspNetUsers'") != 0)
            Assert.Equal(0L, await ScalarAsync(read, "SELECT COUNT(*) FROM AspNetUsers"));
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Onafhankelijke providerread vergelijkt alle domein-/migratierijen als hash, zonder account- of credentialdump.</summary>
    private static async Task<string> RawDataDigestAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var tables = new List<string>();
        await using (var catalog = connection.CreateCommand())
        {
            catalog.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsLock' ORDER BY name";
            await using var reader = await catalog.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }
        var data = new Dictionary<string, List<object?[]>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" ORDER BY rowid";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<object?[]>();
            while (await reader.ReadAsync())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values.Select(value => value is DBNull ? null : value).ToArray());
            }
            data.Add(table, rows);
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(data))));
    }
}
