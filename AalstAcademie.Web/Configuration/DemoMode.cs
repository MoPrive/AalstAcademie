using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AalstAcademie.Web.Configuration;

/// <summary>De effectieve demomode; een configuratievlag zonder Development is nooit voldoende.</summary>
/// <remarks>
/// Eén berekende waarde stuurt bootstrap, aanmelding en toegangscontrole. Zo kan een
/// demo-identiteit buiten deze expliciete ontwikkelomgeving geen gewone accounttoegang krijgen.
/// </remarks>
public sealed record DemoMode(bool IsEnabled)
{
    /// <summary>Valideert de opstartconfiguratie vóór accounts of andere demo-inhoud worden aangemaakt.</summary>
    public static DemoMode From(IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration.GetValue<bool>("Demo:Enabled");
        // Een per ongeluk meegenomen Development-vlag moet buiten Development de opstart stoppen.
        if (configured && !environment.IsDevelopment())
            throw new InvalidOperationException("Demo:Enabled mag uitsluitend in Development worden ingeschakeld.");
        return new DemoMode(configured && environment.IsDevelopment());
    }
}
