// Sprint 003: Injecteert pas na een echte schema-, referentie- of accountwrite een fout
// Daardoor bewijst een test rollback of geweigerde startup na gedeeltelijke SQL-uitvoering.
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Breekt pas na een echte schema-/referentie-/accountwrite; de bootstrap kan geen succes of hint publiceren.</summary>
public sealed class DemoStartupFailureInterceptor : DbCommandInterceptor
{
    private string? point;
    private int registrationInserts, momentInserts, waitlistInserts;
    public bool Triggered { get; private set; }
    public bool SawSqlWrite { get; private set; }
    public void Arm(string point)
    {
        if (point is not ("schema" or "reference" or "account" or "offer" or "enrolment-moment" or "request" or "join" or "workflow-review" or "workflow-refill" or "workflow-create" or "workflow-join")) throw new ArgumentException("Onbekend startupfoutpunt.", nameof(point));
        this.point = point; Triggered = false; SawSqlWrite = false; registrationInserts = momentInserts = waitlistInserts = 0;
    }
    public void Disarm() => point = null;
    private void FailAfterWrite(DbCommand command)
    {
        var sql = command.CommandText;
        if (sql.Contains("INSERT INTO \"Registrations\"", StringComparison.Ordinal)) registrationInserts++;
        if (sql.Contains("INSERT INTO \"TrainingMoments\"", StringComparison.Ordinal)) momentInserts++;
        if (sql.Contains("INSERT INTO \"WaitlistEntries\"", StringComparison.Ordinal)) waitlistInserts++;
        var matches = point switch
        {
            "schema" => sql.Contains("CREATE TABLE \"AspNetRoles\"", StringComparison.Ordinal),
            "reference" => sql.Contains("INSERT INTO \"Departments\"", StringComparison.Ordinal),
            "account" => sql.Contains("INSERT INTO \"AspNetUsers\"", StringComparison.Ordinal),
            // De fixture mag het toekomstige foutpunt al herkennen; aanbodseed en startupcases horen uitsluitend bij C.
            "offer" => sql.Contains("INSERT INTO \"TrainingMoments\"", StringComparison.Ordinal),
            // De aanvullende uitvoering faalt uitsluitend na zijn eigen echte INSERT, niet na de oude vijf momenten.
            "enrolment-moment" => sql.Contains("INSERT INTO \"TrainingMoments\"", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(x => Convert.ToString(x.Value, System.Globalization.CultureInfo.InvariantCulture) == "300406"),
            "request" => sql.Contains("INSERT INTO \"Registrations\"", StringComparison.Ordinal),
            "join" => sql.Contains("INSERT INTO \"WaitlistEntries\"", StringComparison.Ordinal),
            "workflow-review" => sql.Contains("UPDATE \"Registrations\"", StringComparison.Ordinal),
            "workflow-refill" => sql.Contains("INSERT INTO \"Registrations\"", StringComparison.Ordinal) && registrationInserts == 3,
            "workflow-create" => sql.Contains("INSERT INTO \"TrainingMoments\"", StringComparison.Ordinal) && momentInserts == 7,
            "workflow-join" => sql.Contains("INSERT INTO \"WaitlistEntries\"", StringComparison.Ordinal) && waitlistInserts == 2,
            _ => false
        };
        if (!matches) return;
        Triggered = SawSqlWrite = true;
        point = null;
        throw new InvalidOperationException("Geïnjecteerde fout na een echte geïsoleerde startupwrite.");
    }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result) { FailAfterWrite(command); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    { FailAfterWrite(command); return ValueTask.FromResult(result); }
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        try { FailAfterWrite(command); return result; }
        // De na-SQL-fout mag geen RETURNING-reader achterlaten die de echte rollback/cleanup hindert.
        catch { result.Dispose(); throw; }
    }
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        try { FailAfterWrite(command); return result; }
        catch { await result.DisposeAsync(); throw; }
    }
}
