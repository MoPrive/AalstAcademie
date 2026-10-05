# AalstAcademie — demoaccounts

Open in de draaiende demo **Aanmelden** (`/Identity/Account/Login`). Klap **Aanmeldgegevens voor de fictieve demosessie** open. Daar vind je de demoaccounts en het actuele wachtwoord.

| Demoaccount | E-mailadres |
| --- | --- |
| Beheerder | `beheerder@example.invalid` |
| Medewerker | `medewerker@example.invalid` |
| Interne lesgever | `interne-lesgever@example.invalid` |
| Externe lesgever | `externe-lesgever@example.invalid` |
| Medewerker met aanvraag in afwachting | `aanvraag-medewerker@example.invalid` |
| Externe lesgever met aanvraag in afwachting | `aanvraag-externe-lesgever@example.invalid` |

Alle zes demoaccounts gebruiken hetzelfde tijdelijke wachtwoord. Dit wordt bij iedere demo-opstart opnieuw gegenereerd en staat niet vast in de broncode of configuratie. Gebruik het wachtwoord dat de huidige aanmeldpagina toont. Het aanmeldblok verschijnt uitsluitend wanneer de Development-demomodus actief is.

In de solution staat de accountlijst in `AalstAcademie.Web/Data/DemoAccountCatalog.cs`. `AalstAcademie.Web/Data/DemoCredentials.cs` genereert het tijdelijke wachtwoord; `AalstAcademie.Web/Areas/Identity/Pages/Account/Login.cshtml` toont het aanmeldblok.
