using AalstAcademie.Web.Services.Accounts;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Registreert meldpogingen en laat tests de commitgrens of een transportfout onderzoeken.</summary>
public sealed class RecordingAccountApplicationsNotifier : IAccountApplicationNotifier
{
    private int count;
    // Volatile/Interlocked maken het aantal meldpogingen ook bij twee concurrerende reviews betrouwbaar.
    public int Count => Volatile.Read(ref count);
    // De callback onderzoekt de commitgrens; de exception test best-effort melding na een opgeslagen succes.
    public Func<CancellationToken, Task>? OnNotifyAsync { get; set; }
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>Neemt de service-aanroep op; dit bewijst geen echte SignalR-verbinding of browserontvangst.</summary>
    public async Task NotifyChangedAsync(CancellationToken cancellationToken = default)
    {
        // Ook een mislukte publicatie is een poging; een rollback mag deze methode nooit bereiken.
        Interlocked.Increment(ref count);
        if (OnNotifyAsync is not null) await OnNotifyAsync(cancellationToken);
        if (ExceptionToThrow is not null) throw ExceptionToThrow;
    }
}
