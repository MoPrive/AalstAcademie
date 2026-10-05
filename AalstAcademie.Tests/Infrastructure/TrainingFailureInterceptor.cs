// Sprint 003: Onderbreekt een expliciet geactiveerde test na echte SQLite-writes
// De volgende onafhankelijke readback bewijst dat parent- en childwijzigingen samen zijn teruggedraaid.
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Eenmalige fout na echte SQLite-writes, uitsluitend na expliciete activering in een test.</summary>
public sealed class TrainingFailureInterceptor : DbCommandInterceptor
{
    private string? point;
    public bool Triggered { get; private set; }
    public bool SawParentWrite { get; private set; }
    public bool SawSqlWrite { get; private set; }
    public void Arm(string failurePoint)
    {
        if (failurePoint is not ("create-parent" or "update-parent" or "audience-link" or "category-save" or
            "location-create" or "location-update" or "location-delete" or "moment-create" or "moment-planning" or "moment-capacity"))
            throw new ArgumentException("Onbekende fictieve foutgrens.", nameof(failurePoint));
        point = failurePoint; Triggered = false; SawParentWrite = false; SawSqlWrite = false;
    }
    public void Disarm() => point = null;
    private bool AfterExecuted(DbCommand command)
    {
        if (point is null) return false;
        var sql = command.CommandText;
        var insertParent = sql.Contains("INSERT INTO \"Trainings\"", StringComparison.Ordinal);
        var updateParent = sql.Contains("UPDATE \"Trainings\"", StringComparison.Ordinal);
        var category = sql.Contains("UPDATE \"Categories\"", StringComparison.Ordinal);
        SawSqlWrite |= sql.Contains("INSERT INTO", StringComparison.Ordinal) || sql.Contains("UPDATE ", StringComparison.Ordinal) || sql.Contains("DELETE FROM", StringComparison.Ordinal);
        if (insertParent || updateParent || category) SawParentWrite = true;
        var fail = point switch
        {
            "create-parent" => insertParent,
            "audience-link" => sql.Contains("INSERT INTO \"TrainingDepartments\"", StringComparison.Ordinal),
            "update-parent" => updateParent,
            "category-save" => category,
            "location-create" => sql.Contains("INSERT INTO \"Locations\"", StringComparison.Ordinal),
            "location-update" => sql.Contains("UPDATE \"Locations\"", StringComparison.Ordinal),
            "location-delete" => sql.Contains("DELETE FROM \"Locations\"", StringComparison.Ordinal),
            "moment-create" => sql.Contains("INSERT INTO \"TrainingMoments\"", StringComparison.Ordinal),
            "moment-planning" or "moment-capacity" => sql.Contains("UPDATE \"TrainingMoments\"", StringComparison.Ordinal),
            _ => false
        };
        if (fail) { Triggered = true; point = null; }
        return fail;
    }
    // RETURNING-reader ontstaat pas na de SQLite-write. Sluit die vóór throw zodat rollback de verbinding vrij krijgt.
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        if (AfterExecuted(command)) { result.Dispose(); throw new InvalidOperationException("Fictieve fout na echte SQL-write."); }
        return result;
    }
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (AfterExecuted(command)) { await result.DisposeAsync(); throw new InvalidOperationException("Fictieve fout na echte SQL-write."); }
        return result;
    }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        if (AfterExecuted(command)) throw new InvalidOperationException("Fictieve fout na echte SQL-write.");
        return result;
    }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        if (AfterExecuted(command)) throw new InvalidOperationException("Fictieve fout na echte SQL-write.");
        return ValueTask.FromResult(result);
    }
}
