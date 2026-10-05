using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Injecteert één fout na werkelijk uitgevoerde SQL, zodat de hele veroorzakende transactie moet terugrollen.</summary>
public sealed class ParticipationWorkflowFailureInterceptor : DbCommandInterceptor
{
    private string? sql;
    private Action? action;
    public bool Triggered { get; private set; }
    public void Arm(string fragment) { sql = fragment; action = null; Triggered = false; }
    public void ArmAction(string fragment, Action callback) { sql = fragment; action = callback; Triggered = false; }
    private bool Match(DbCommand command)
    {
        if (sql is null || !command.CommandText.Contains(sql, StringComparison.Ordinal)) return false;
        sql = null; Triggered = true; return true;
    }
    private void Fail()
    {
        if (action is not null) { var callback = action; action = null; callback(); return; }
        throw new SqliteException("Geïsoleerde workflowfout na echte SQL.", 1);
    }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData data, int result)
    { if (Match(command)) Fail(); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken cancellationToken = default)
    { if (Match(command)) Fail(); return ValueTask.FromResult(result); }
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData data, DbDataReader result)
    { if (Match(command)) { if (action is null) result.Dispose(); Fail(); } return result; }
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default)
    { if (Match(command)) { if (action is null) await result.DisposeAsync(); Fail(); } return result; }
}
