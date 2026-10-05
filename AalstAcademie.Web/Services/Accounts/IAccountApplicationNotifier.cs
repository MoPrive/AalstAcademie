namespace AalstAcademie.Web.Services.Accounts;

/// <summary>Een lege invalidatie laat bevoegde schermen na commit hun eigen actuele snapshot ophalen.</summary>
public interface IAccountApplicationNotifier
{
    /// <summary>Signaleert gewijzigde gegevens zonder persoonsgegevens of beslisgegevens als payload te sturen.</summary>
    Task NotifyChangedAsync(CancellationToken cancellationToken = default);
}

/// <summary>Een tijdelijk notificatieprobleem mag een reeds opgeslagen beslissing niet terugdraaien.</summary>
internal static class AccountApplicationNotification
{
    /// <summary>Probeert een schermmelding pas na commit; bezorging is geen voorwaarde voor opgeslagen succes.</summary>
    /// <remarks>
    /// Deze best-effort melding is geen duurzame berichtenwachtrij. Na een gemiste melding
    /// haalt de browser via reconnect, tabterugkeer of handmatig vernieuwen opnieuw een snapshot op.
    /// </remarks>
    public static async Task NotifyCommittedAsync(this IAccountApplicationNotifier notifier, ILogger logger)
    {
        // Een onafhankelijk, begrensd token voorkomt dat requestannulering durable succes vervalst.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await notifier.NotifyChangedAsync(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception)
        {
            // Geen exceptiondetails: die kunnen onbedoeld persoonsgegevens bevatten.
            logger.LogWarning("Accountwijziging opgeslagen; de schermmelding kon niet worden afgeleverd.");
        }
    }
}
