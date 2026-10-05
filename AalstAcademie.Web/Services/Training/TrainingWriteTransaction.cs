// Sprint 003: Reserveert de SQLite-schrijver vóór guardreads en commit alleen succes
// CAS beschermt dezelfde rij; de gezamenlijke write-lock beschermt invarianten over opleiding, historie, moment en zaal.
using System.Data;
using System.Data.Common;
using AalstAcademie.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AalstAcademie.Web.Services.Training;

/// <summary>Reserveert de SQLite-schrijver vóór lezen en bewaakt één atomische featuremutatie.</summary>
public sealed class TrainingWriteTransaction(ApplicationDbContext context)
{
    /// <summary>Commit alleen een geslaagde operatie; fouten herstellen opslag, tracking en tijdelijke timeoutinstellingen.</summary>
    public async Task<TrainingOperationResult> ExecuteAsync(
        Func<CancellationToken, Task<TrainingOperationResult>> operation,
        CancellationToken cancellationToken = default, bool categoryNameUniqueness = false)
    {
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Opleidingsbeheer vereist een eigen schrijftransactie.");
        if (context.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("Opleidingsbeheer vereist de geconfigureerde SQLite-provider.");
        var wasOpen = connection.State == ConnectionState.Open;
        var previousDefaultTimeout = connection.DefaultTimeout;
        var previousCommandTimeout = context.Database.GetCommandTimeout();
        SqliteTransaction? nativeTransaction = null;
        IDbContextTransaction? enlistment = null;
        var completed = false;
        try
        {
            connection.DefaultTimeout = 5;
            context.Database.SetCommandTimeout(5);
            if (!wasOpen) await context.Database.OpenConnectionAsync(cancellationToken);
            // deferred:false neemt direct de write-lock; rechten en ExpectedVersion worden pas daarna gelezen.
            // CAS alleen beschermt één rij. De vroege gezamenlijke SQLite-lock voorkomt ook conflicterende wijzigingen tussen verschillende tabellen.
            nativeTransaction = connection.BeginTransaction(deferred: false);
            enlistment = await context.Database.UseTransactionAsync(nativeTransaction, cancellationToken);
            var result = await operation(cancellationToken);
            if (result.Succeeded)
                await nativeTransaction.CommitAsync(cancellationToken);
            else
            {
                await nativeTransaction.RollbackAsync(CancellationToken.None);
                context.ChangeTracker.Clear();
            }
            completed = true;
            return result;
        }
        // Een verloren CAS-race wordt een gecontroleerd conflict; finally draait eventuele eerdere writes terug.
        catch (DbUpdateConcurrencyException)
        {
            return new(TrainingOperationStatus.Conflict);
        }
        catch (DbUpdateException exception) when (categoryNameUniqueness && IsUniqueNameCollision(exception.InnerException))
        {
            return DuplicateName();
        }
        catch (SqliteException exception) when (categoryNameUniqueness && IsUniqueNameCollision(exception))
        {
            return DuplicateName();
        }
        catch (DbUpdateException)
        {
            // Ook een fout na een echte parentwrite blijft een mislukte operatie, nooit gedeeltelijk succes.
            return new(TrainingOperationStatus.StorageUnavailable);
        }
        catch (DbException)
        {
            // SQLITE_BUSY/LOCKED en overige opslagfouten krijgen dezelfde veilige, begrensde hersteluitkomst.
            return new(TrainingOperationStatus.StorageUnavailable);
        }
        finally
        {
            try
            {
                try
                {
                    if (!completed && nativeTransaction is not null)
                    {
                        try { await nativeTransaction.RollbackAsync(CancellationToken.None); }
                        catch (DbException) { /* Disposal beëindigt een al afgebroken opslagtransactie. */ }
                    }
                }
                finally
                {
                    try
                    {
                        if (!completed) context.ChangeTracker.Clear();
                    }
                    finally
                    {
                        // Beide eigenaars krijgen een disposalpoging, ook als de EF-wrapper cleanup afwijst.
                        try { if (enlistment is not null) await enlistment.DisposeAsync(); }
                        finally { if (nativeTransaction is not null) await nativeTransaction.DisposeAsync(); }
                    }
                }
            }
            finally
            {
                // Tijdelijke instellingen worden hersteld buiten rollback/disposal: hun uitzonderingen mogen dit niet overslaan.
                try { context.Database.SetCommandTimeout(previousCommandTimeout); }
                finally
                {
                    try { connection.DefaultTimeout = previousDefaultTimeout; }
                    finally
                    {
                        // Een bestaande open geheugendatabase hoort bij de fixture/caller en wordt hier niet gesloten.
                        if (!wasOpen && connection.State != ConnectionState.Closed) await context.Database.CloseConnectionAsync();
                    }
                }
            }
        }
    }

    private static bool IsUniqueNameCollision(Exception? exception) =>
        exception is SqliteException { SqliteExtendedErrorCode: 2067 };

    private static TrainingOperationResult DuplicateName() => new(TrainingOperationStatus.Invalid, Errors:
        new Dictionary<string, string[]> { ["Name"] = ["Deze categorienaam bestaat al."] });
}
