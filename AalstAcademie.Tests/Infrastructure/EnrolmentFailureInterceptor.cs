using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Fout pas nadat SQLite de bedoelde SQL echt uitvoert; een andere scope bewijst volledige rollback.</summary>
public sealed class EnrolmentFailureInterceptor : DbCommandInterceptor
{
    private string? point;
    public bool Triggered { get; private set; }
    public void Arm(string value)
    {
        if (value is not ("request" or "join" or "withdraw" or "manager" or "responsible"))
            throw new ArgumentException("Onbekende geïsoleerde foutgrens.", nameof(value));
        point = value; Triggered = false;
    }
    private bool Match(DbCommand command)
    {
        var match = point switch
        {
            "request" => "INSERT INTO \"Registrations\"",
            "join" => "INSERT INTO \"WaitlistEntries\"",
            "withdraw" => "UPDATE \"WaitlistEntries\"",
            "manager" => "UPDATE \"AspNetUsers\"",
            "responsible" => "UPDATE \"Departments\"",
            _ => null
        };
        if (match is null || !command.CommandText.Contains(match, StringComparison.Ordinal)) return false;
        point = null; Triggered = true; return true;
    }
    private static void Fail() => throw new SqliteException("Geïsoleerde testfout na echte SQL.", 1);
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData data, int result)
    { if (Match(command)) Fail(); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken cancellationToken = default)
    { if (Match(command)) Fail(); return ValueTask.FromResult(result); }
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData data, DbDataReader result)
    { if (Match(command)) { result.Dispose(); Fail(); } return result; }
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default)
    { if (Match(command)) { await result.DisposeAsync(); Fail(); } return result; }
}
