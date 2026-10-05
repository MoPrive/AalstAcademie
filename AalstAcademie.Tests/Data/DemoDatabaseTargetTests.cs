// Sprint 003: Toetst pure pad- en configuratievalidatie, inclusief aliases en ongeldige doelen
// Het bepalen van een pad mag nog geen SQLite-bestand aanmaken.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AalstAcademie.Tests.Data;

/// <summary>Het resetdoel komt uitsluitend uit de gevalideerde projectroot en expliciete SQLite-configuratie.</summary>
public class DemoDatabaseTargetTests
{
    [Fact]
    public void Relative_database_and_reset_target_resolve_against_content_root_without_opening_database()
    {
        using var directory = new TemporaryTestDirectory("demo-target");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Data Source=own-demo.db;Pooling=False;Foreign Keys=True",
            ["Demo:ResetTargetPath"] = "own-demo.db"
        }).Build();
        var target = DemoDatabaseTarget.From(new TestEnvironment(directory.DirectoryPath), configuration, new DemoMode(true));

        Assert.Equal(directory.CreatePath("own-demo.db"), target.DatabasePath);
        Assert.Equal(target.DatabasePath, new SqliteConnectionStringBuilder(target.ConnectionString).DataSource);
        Assert.Equal(target.DatabasePath + ".application.lock", target.LeasePath);
        // Pure padvalidatie mag zelfs nog geen leeg SQLite-bestand of leasebestand aanmaken.
        Assert.False(File.Exists(target.DatabasePath));
        Assert.False(File.Exists(target.LeasePath));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("absolute")]
    [InlineData("case-dot-alias")]
    public void Equivalent_paths_select_the_same_canonical_database(string variant)
    {
        using var directory = new TemporaryTestDirectory("demo-target");
        var expected = directory.CreatePath("own-demo.db");
        var source = variant switch
        {
            "relative" => "own-demo.db",
            "absolute" => expected,
            _ => Path.Combine(directory.DirectoryPath, ".", OperatingSystem.IsWindows() ? "OWN-DEMO.DB" : "own-demo.db")
        };
        var target = DemoDatabaseTarget.From(new TestEnvironment(directory.DirectoryPath), Settings(source, expected), new DemoMode(true));
        Assert.True(string.Equals(expected, target.DatabasePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.False(File.Exists(expected));
    }

    [Theory]
    [InlineData("missing-datasource")]
    [InlineData("missing-reset")]
    [InlineData("mismatch")]
    [InlineData("memory")]
    [InlineData("named-memory")]
    [InlineData("uri")]
    [InlineData("readonly")]
    [InlineData("provider")]
    [InlineData("unc")]
    [InlineData("reparse")]
    [InlineData("invalid-connection")]
    public void Unsafe_or_ambiguous_target_is_rejected_before_any_database_or_lease_write(string variant)
    {
        using var directory = new TemporaryTestDirectory("demo-target");
        var expected = directory.CreatePath("own-demo.db");
        var source = expected;
        var reset = expected;
        var connection = "Data Source=" + source + ";Pooling=False";
        switch (variant)
        {
            case "missing-datasource": connection = "Pooling=False"; break;
            case "missing-reset": reset = null!; break;
            case "mismatch": reset = directory.CreatePath("other.db"); break;
            case "memory": connection = "Data Source=:memory:"; break;
            case "named-memory": connection = "Data Source=own-memory;Mode=Memory"; break;
            case "uri": connection = "Data Source=file:own-demo.db?mode=rwc"; break;
            case "readonly": connection += ";Mode=ReadOnly"; break;
            case "provider": connection = "Server=localhost;Database=own-demo"; break;
            case "unc": connection = @"Data Source=\\localhost\own-demo\demo.db"; break;
            case "reparse":
                var real = directory.CreatePath("real");
                Directory.CreateDirectory(real);
                var link = directory.CreatePath("junction");
                CreateDirectoryLink(link, real);
                connection = "Data Source=" + Path.Combine(link, "demo.db");
                reset = Path.Combine(link, "demo.db");
                break;
            case "invalid-connection": connection = "Data Source=\"unfinished"; break;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DefaultConnection"] = connection, ["Demo:ResetTargetPath"] = reset }).Build();
        Assert.Throws<InvalidOperationException>(() => DemoDatabaseTarget.From(new TestEnvironment(directory.DirectoryPath), configuration, new DemoMode(true)));
        Assert.False(File.Exists(expected));
        Assert.False(File.Exists(expected + ".application.lock"));
        if (variant == "invalid-connection" && OperatingSystem.IsWindows())
        {
            // Dezelfde getelde ongeldige-doelvariant bewijst ook Win32-aliasen en devicenamen, zonder ze te openen.
            var unsafePaths = new[]
            {
                "CON.db", "nul", "PRN.db", "AUX.db", "COM1.db", "LPT9.db", "COM¹.db", "LPT².db", "COM³.db",
                "CONIN$.db", "CONOUT$.db", "bad?.db", "bad|name.db", "bad:name.db", "bad\u0001.db"
            }.Select(fileName => Path.Combine(directory.DirectoryPath, fileName)).Concat(new[]
            {
                expected + ".", expected + " ",
                Path.Combine(directory.DirectoryPath, "alias.", "demo.db"),
                Path.Combine(directory.DirectoryPath, "alias ", "demo.db"),
                Path.Combine(directory.DirectoryPath, "LPT2.folder", "demo.db")
            });
            foreach (var unsafePath in unsafePaths)
            {
                // Matching reset/config zou anders dezelfde echte DB onder een andere lease kunnen aanspreken.
                var unsafeSettings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = new SqliteConnectionStringBuilder { DataSource = unsafePath }.ToString(),
                    ["Demo:ResetTargetPath"] = unsafePath
                }).Build();
                Assert.Throws<InvalidOperationException>(() => DemoDatabaseTarget.From(new TestEnvironment(directory.DirectoryPath), unsafeSettings, new DemoMode(true)));
            }
            Assert.False(File.Exists(expected));
            Assert.False(File.Exists(expected + ".application.lock"));
        }
    }

    [Fact]
    public void Non_demo_ignores_the_reset_setting_and_only_normalizes_its_actual_connection()
    {
        using var directory = new TemporaryTestDirectory("demo-target");
        var expected = directory.CreatePath("own-normal.db");
        var target = DemoDatabaseTarget.From(new TestEnvironment(directory.DirectoryPath), Settings(expected, "file:invalid-reset"), new DemoMode(false));
        Assert.Equal(expected, target.DatabasePath);
        Assert.False(File.Exists(expected));
    }

    private static IConfiguration Settings(string source, string reset) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = "Data Source=" + source + ";Pooling=False", ["Demo:ResetTargetPath"] = reset }).Build();

    /// <summary>Een echte eigen junction vereist geen Developer Mode en start geen shell; beide doelen blijven binnen dezelfde GUID-map.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        Directory.CreateDirectory(link);
        using var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var dataLength = checked((ushort)(8 + substitute.Length + 2 + print.Length + 2));
        var buffer = new byte[dataLength + 8];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes(dataLength).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);
        if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int bytesReturned, IntPtr overlapped);

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "AalstAcademie.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
