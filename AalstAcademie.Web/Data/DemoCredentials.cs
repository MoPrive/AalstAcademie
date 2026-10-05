// Sprint 003: Bewaart één nieuw cryptografisch gegenereerd sessiewachtwoord in geheugen
// De loginhint verschijnt pas na complete bootstrap; algemene objectweergave schrijft het geheim niet uit.
using System.Security.Cryptography;

namespace AalstAcademie.Web.Data;

/// <summary>Singleton voor één tijdelijk demosessiewachtwoord; lezen genereert nooit een geheim.</summary>
public sealed class DemoCredentials
{
    private readonly object gate = new();
    private string? password;
    private bool accountsComplete;
    private DemoCredentialHint? readyHint;
    public bool IsReady { get { lock (gate) return readyHint is not null; } }

    // Genereer tijdens accountopbouw en onder dezelfde geheugenlock; het Login-scherm mag geen nieuw wachtwoord maken.
    internal string PrepareAccountBatch()
    {
        lock (gate)
        {
            if (accountsComplete || readyHint is not null) throw new InvalidOperationException("De demoaccountbatch is reeds opgebouwd.");
            const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "!@$%*?-_";
            const string alphabet = upper + lower + digits + symbols;
            var characters = new char[12];
            characters[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
            characters[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
            characters[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
            characters[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
            for (var i = 4; i < characters.Length; i++) characters[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            return password = new string(characters);
        }
    }

    // Dit markeert alleen de accountfase. De bovenliggende bootstrap wacht ook op het aanbod voordat zij de hint publiceert.
    internal void CompleteAccountBatch() { lock (gate) accountsComplete = password is not null; }

    /// <summary>De caller geeft de gecommitte accountbatch uitsluitend na alle bootstrapstappen vrij.</summary>
    public void PublishReady(DemoSeedResult result)
    {
        lock (gate)
        {
            if (!result.IsSeeded || result.CreatedAccountCount != DemoAccountCatalog.All.Count || !accountsComplete || password is null)
                throw new InvalidOperationException("Aanmeldgegevens worden alleen na volledige demo-opbouw gepubliceerd.");
            readyHint = new DemoCredentialHint(DemoAccountCatalog.All.Select(x => x.Email).ToArray(), password);
        }
    }

    // Geef alleen de reeds gepubliceerde hint terug. Een leesaanroep verandert de startupstatus of het wachtwoord niet.
    public bool TryGetReadyHint(out DemoCredentialHint? hint) { lock (gate) { hint = readyHint; return hint is not null; } }
    public void Clear() { lock (gate) { readyHint = null; password = null; accountsComplete = false; } }
    public override string ToString() => "Tijdelijke demo-aanmeldgegevens in sessiegeheugen";
}

/// <summary>Alleen voor de gelabelde ongecachete Login-hint; objectinterpolatie toont geen inhoud.</summary>
public sealed class DemoCredentialHint(IReadOnlyList<string> emails, string password)
{
    public IReadOnlyList<string> Emails { get; } = Array.AsReadOnly(emails.ToArray());
    public string Password { get; } = password;
    public override string ToString() => "Gepubliceerde fictieve demo-aanmeldhint";
}
