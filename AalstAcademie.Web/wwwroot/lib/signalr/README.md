# SignalR browserclient

Officiële @microsoft/signalr **10.0.0**, lokaal gebundeld onder MIT. Geen runtime-CDN of npm-build vereist.

Bron: [npm-register](https://registry.npmjs.org/@microsoft/signalr/10.0.0), [pakketarchief](https://registry.npmjs.org/@microsoft/signalr/-/signalr-10.0.0.tgz), [upstreamlicentie v10.0.0](https://github.com/dotnet/aspnetcore/blob/v10.0.0/LICENSE.txt).

Het officiële archief bevat de twee browserassets, maar geen aparte licentietekst. LICENSE.txt is daarom rechtstreeks uit dezelfde upstreamversietag gehaald. De assets zijn ongewijzigd gekopieerd; geen package-scripts uitgevoerd.

Gecontroleerde npm SHA512-integriteit van het archief:

```text
sha512-0BRqz/uCx3JdrOqiqgFhih/+hfTERaUfCZXFB52uMaZJrKaPRzHzMuqVsJC/V3pt7NozcNXGspjKiQEK+X7P2w==
```

SHA256 van de gebundelde bestanden:

```text
signalr.min.js     10DD7681421E4638A55A3367A68AC0EAC1973BE1B4471B91B1B9E2306BB0B050
signalr.min.js.map 85D0AC265CB7F9678B0FDED4C52DFD7717BAEDE729DD61382C2CB048809F2FF7
```

De server zit in het ASP.NET Core shared framework. Het eigen account-applications.js gebruikt een lege invalidatie en haalt gegevens uitsluitend uit de beveiligde snapshot.
