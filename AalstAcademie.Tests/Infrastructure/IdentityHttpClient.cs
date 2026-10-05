using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace AalstAcademie.Tests.Infrastructure;

/// <summary>Een echte cookiehoudende HTTP-client die antiforgery uit het gerenderde formulier overneemt.</summary>
public sealed class IdentityHttpClient(HttpClient client) : IDisposable
{
    // RawClient blijft beschikbaar voor negatieve tests die het CSRF-token opzettelijk weglaten.
    public HttpClient RawClient { get; } = client;
    /// <summary>Behoudt de cookiecontainer van deze browser tijdens gewone navigatie.</summary>
    public Task<HttpResponseMessage> GetAsync(string path) => RawClient.GetAsync(path);

    /// <summary>Gebruikt het echte Razor-loginformulier; er wordt geen testprincipal of fake cookie gemaakt.</summary>
    public Task<HttpResponseMessage> LoginAsync(TestIdentity identity) => PostFormAsync("/Identity/Account/Login", new Dictionary<string, string>
    {
        ["Input.Email"] = identity.Email, ["Input.Password"] = identity.Password, ["Input.RememberMe"] = "false"
    });

    /// <summary>Markeert snapshotverkeer zodat toegangsverlies een 401/403-status geeft in plaats van HTML-navigatie.</summary>
    public async Task<HttpResponseMessage> GetAjaxAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await RawClient.SendAsync(request);
    }

    /// <summary>Haalt een vers token onder de huidige cookie-identiteit en post daarna echte formulierdata.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields, string? tokenPage = null)
    {
        var token = await GetAntiforgeryTokenAsync(tokenPage ?? path);
        var posted = new Dictionary<string, string>(fields, StringComparer.Ordinal)
        {
            ["__RequestVerificationToken"] = token
        };
        return await RawClient.PostAsync(path, new FormUrlEncodedContent(posted));
    }

    /// <summary>Leest het hidden token uit de response; dezelfde client bewaart de vereiste antiforgery-cookie.</summary>
    public async Task<string> GetAntiforgeryTokenAsync(string path)
    {
        using var page = await RawClient.GetAsync(path);
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        foreach (Match input in Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var name = Regex.Match(input.Value, "\\bname\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase);
            if (name.Groups[2].Value != "__RequestVerificationToken") continue;
            var value = Regex.Match(input.Value, "\\bvalue\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase);
            if (value.Success) return WebUtility.HtmlDecode(value.Groups[2].Value);
        }
        // Een plain niet-beschikbare registratiepagina heeft geen form; het toegestane loginformulier
        // kan nog steeds een echt token leveren voor het beveiligde rechtstreekse POST-bewijs.
        if (!path.Equals("/Identity/Account/Login", StringComparison.OrdinalIgnoreCase))
            return await GetAntiforgeryTokenAsync("/Identity/Account/Login");
        throw new InvalidOperationException("Het echte HTTP-formulier bevat geen antiforgery-token.");
    }

    /// <summary>Beëindigt deze testbrowser zonder cookies van andere clients of gebruikersapps te wijzigen.</summary>
    public void Dispose() => RawClient.Dispose();
}
