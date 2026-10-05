// Sprint 003: Gebruikt echte bestanden om exclusieve eigendom van een databasedoel en de controles op database/WAL/SHM te bewijzen
// Disposal geeft alleen de eigen lease vrij.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AalstAcademie.Tests.Data;

/// <summary>Een echte exclusieve filelease bewaakt hetzelfde doel over onafhankelijke applicatie-instanties.</summary>
public class DemoDatabaseLeaseTests
{
    [Fact]
    public void Second_lease_for_the_same_canonical_database_is_refused_without_creating_database()
    {
        using var directory = new TemporaryTestDirectory("demo-lease");
        var databasePath = directory.CreatePath("own-demo.db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath};Pooling=False;Foreign Keys=True",
            ["Demo:ResetTargetPath"] = databasePath
        }).Build();
        var target = DemoDatabaseTarget.From(new TestEnvironment(directory.DirectoryPath), configuration, new DemoMode(true));

        using var first = DemoDatabaseLease.Acquire(target);
        Assert.Throws<InvalidOperationException>(() => DemoDatabaseLease.Acquire(target));
        Assert.False(File.Exists(target.DatabasePath));
    }

    [Fact]
    public void Canonical_alias_contends_for_the_same_lease()
    {
        using var environment = new DemoStartupTestEnvironment();
        var original = DemoDatabaseTarget.From(environment, environment.Configuration(), new DemoMode(true));
        var settings = environment.Settings();
        settings["ConnectionStrings:DefaultConnection"] = "Data Source=" + Path.Combine(Path.GetDirectoryName(original.DatabasePath)!, ".", "demo.db");
        var alias = DemoDatabaseTarget.From(environment, new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new DemoMode(true));
        using var first = DemoDatabaseLease.Acquire(original);
        Assert.Throws<InvalidOperationException>(() => DemoDatabaseLease.Acquire(alias));
    }

    [Fact]
    public void Different_database_targets_can_be_leased_at_the_same_time()
    {
        using var firstEnvironment = new DemoStartupTestEnvironment();
        using var secondEnvironment = new DemoStartupTestEnvironment();
        using var first = DemoDatabaseLease.Acquire(DemoDatabaseTarget.From(firstEnvironment, firstEnvironment.Configuration(), new DemoMode(true)));
        using var second = DemoDatabaseLease.Acquire(DemoDatabaseTarget.From(secondEnvironment, secondEnvironment.Configuration(), new DemoMode(true)));
        Assert.False(File.Exists(first.Target.DatabasePath));
        Assert.False(File.Exists(second.Target.DatabasePath));
    }

    [Fact]
    public void Disposal_releases_the_lease_for_a_later_instance()
    {
        using var environment = new DemoStartupTestEnvironment();
        var target = DemoDatabaseTarget.From(environment, environment.Configuration(), new DemoMode(true));
        DemoDatabaseLease.Acquire(target).Dispose();
        using var next = DemoDatabaseLease.Acquire(target);
        Assert.False(File.Exists(target.DatabasePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public void Held_provider_file_refuses_probe_without_partial_deletion(string suffix)
    {
        using var environment = new DemoStartupTestEnvironment();
        var target = DemoDatabaseTarget.From(environment, environment.Configuration(), new DemoMode(true));
        using var lease = DemoDatabaseLease.Acquire(target);
        var suffixes = new[] { "", "-wal", "-shm", "-journal" };
        foreach (var item in suffixes) File.WriteAllBytes(target.DatabasePath + item, [1, 2, 3, 4]);
        using (var held = new FileStream(target.DatabasePath + suffix, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<InvalidOperationException>(() => lease.ProbeResetFiles());
        foreach (var item in suffixes) Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(target.DatabasePath + item));
        // Iedere bestaande suffixvariant bewijst ook dat een directory niet stil als ontbrekende sidecar telt.
        File.Delete(target.DatabasePath + suffix);
        Directory.CreateDirectory(target.DatabasePath + suffix);
        var sentinel = Path.Combine(target.DatabasePath + suffix, "bewaard.txt");
        File.WriteAllBytes(sentinel, [5, 6, 7, 8]);
        Assert.Throws<InvalidOperationException>(() => lease.ProbeResetFiles());
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, File.ReadAllBytes(sentinel));
        foreach (var item in suffixes.Where(item => item != suffix))
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(target.DatabasePath + item));
        File.Delete(sentinel);
        Directory.Delete(target.DatabasePath + suffix);
        File.WriteAllBytes(target.DatabasePath + suffix, [1, 2, 3, 4]);
        // De mainvariant omvat daarnaast een echte native SQLite-verbinding met een open WAL-reader.
        if (suffix != "") return;
        foreach (var item in suffixes) File.Delete(target.DatabasePath + item);
        using (var prepareConnection = new SqliteConnection(environment.ConnectionString))
        {
            prepareConnection.Open();
            using var prepare = prepareConnection.CreateCommand();
            prepare.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE Probe(Value INTEGER); INSERT INTO Probe VALUES(7);";
            prepare.ExecuteNonQuery();
        }
        // Het baselinebestand wordt pas gelezen nadat de voorbereidingsverbinding zijn native handles vrijgaf.
        var before = File.ReadAllBytes(target.DatabasePath);
        using (var connection = new SqliteConnection(environment.ConnectionString))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Value FROM Probe";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(7L, reader.GetInt64(0));
            Assert.Throws<InvalidOperationException>(() => lease.ProbeResetFiles());
            Assert.True(File.Exists(target.DatabasePath));
            // Dezelfde reader blijft bruikbaar: de geweigerde probe heeft zijn databank niet verwijderd.
            Assert.Equal(7L, reader.GetInt64(0));
        }
        // Bytevergelijking na disposal voorkomt dat het bewijs zichzelf op de eigen exclusieve native handle blokkeert.
        Assert.Equal(before, File.ReadAllBytes(target.DatabasePath));
        using var independentRead = new SqliteConnection(new SqliteConnectionStringBuilder(environment.ConnectionString)
        { Mode = SqliteOpenMode.ReadOnly }.ToString());
        independentRead.Open();
        using var readBack = independentRead.CreateCommand();
        readBack.CommandText = "SELECT Value FROM Probe";
        Assert.Equal(7L, Convert.ToInt64(readBack.ExecuteScalar()));
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "AalstAcademie.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
