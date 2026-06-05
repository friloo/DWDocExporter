<div align="center">

# 📤 DwDocExport

**Exportiert Dokumente aus DocuWare im Originalformat – Cloud & On-Premise, GUI & Windows-Dienst.**

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows&logoColor=white)](#-systemvoraussetzungen)
[![DocuWare](https://img.shields.io/badge/DocuWare-Platform%20REST%20API-005CA9)](https://developer.docuware.com/rest/index.html)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](#-lizenz)

</div>

---

**DwDocExport** holt **alle Dokumente** eines DocuWare-Aktenschranks über die
offizielle **Platform REST API** heraus – und zwar **im Originalformat** und für
**jeden Dateityp** (PDF, MSG, EML, DOCX, XLSX, JPG, ZIP …). Der Download nutzt
`targetFileType=Auto`, sodass DocuWare die Originaldatei liefert; der Dateiname
kommt aus dem `Content-Disposition`-Header.

> 💡 **Nichts ist auf E-Mails fest verdrahtet.** Der erste Anwendungsfall ist ein
> **Mailarchiv (EML/MSG)**, aber das Tool funktioniert mit jedem Schrank und jedem
> Dateityp.

---

## ✨ Features

- 🗂️ **Vollständiger Export** eines Aktenschranks im **Originalformat**, dateityp-neutral
- ☁️🏢 **Cloud *und* On-Premise** – Token-Login (Identity Service) **und** klassischer Cookie-Login
- 🔀 **`AuthMode=Auto`** wählt automatisch das richtige Verfahren (Token → Fallback Cookie)
- 🖥️ **Eine EXE, zwei Modi**: komfortable **GUI** zum Einrichten + robuster **Windows-Dienst** zum Exportieren
- 🔁 **Fortsetzbar & ohne Doppel-Downloads** dank SQLite-Statusdatenbank (WAL)
- 🧱 **Atomare Schreibvorgänge** (`*.part` → umbenennen) – keine halben Dateien bei Abbruch
- 📅 **Ordnerstruktur nach Datum** (`Jahr/Monat`) plus optionale **Hash-Unterordner**
- ♻️ **Retry mit Backoff** bei `429`/`5xx`, **automatische Neuanmeldung** bei `401`
- ⏱️ **Periodisches Nachscannen** für laufende Archivierung (oder einmaliger Lauf)
- 🧾 **Logging** ins Windows-Ereignisprotokoll; Fortschritt & Fehler live in der GUI

---

## 📸 Oberfläche

> _Screenshot-Platzhalter – hier später ein Bild der GUI einfügen, z. B.
> `docs/screenshot.png`._

```
┌────────────────────────────────────────────────────────────┐
│ DwDocExport – DocuWare Dokument-Export                       │
├────────────────────────────────────────────────────────────┤
│ Server:            https://ihr-server.docuware.cloud         │
│ Organisation:      MEINE-ORG                                 │
│ Benutzer:          api-user        Passwort: ••••••••        │
│ Authentifizierung: [ Auto ▾ ]                                │
│ [ Anmelden / Schränke laden ]   [ Verbindung testen ]        │
│ Aktenschrank:      [ Mailarchiv ▾ ]                          │
│ Datumsfeld:        [ DOCUMENT_DATE ▾ ]  [ Indexfelder laden ]│
│ Ausgabeordner:     C:\Export\DocuWare                        │
│ … weitere Einstellungen …                  [ Speichern ]     │
├──  Dienststeuerung  ─────────────────────────────────────────┤
│ [Installieren] [Starten] [Stoppen] [Deinstallieren]          │
│ Dienststatus: Running     Fortschritt: 1.284 erledigt, 0 Fhl │
├──  Meldungen  ───────────────────────────────────────────────┤
│ [10:21:03] Anmeldung über Identity Service (Token) ok.       │
│ [10:21:04] 3 Aktenschrank/Schränke geladen.                  │
└────────────────────────────────────────────────────────────┘
```

---

## 🚀 Schnellstart

1. **Bauen** (auf einem Windows-PC mit .NET 8 SDK):
   ```powershell
   dotnet publish -c Release -r win-x64 --self-contained true `
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
   ```
   → fertige Einzel-EXE unter
   `bin\Release\net8.0-windows\win-x64\publish\DwDocExport.exe`
2. **EXE auf den Zielrechner kopieren** und per Doppelklick starten (GUI).
   Beim Start einmal die **UAC-Abfrage** bestätigen (Adminrechte für die Dienstverwaltung).
3. In der GUI: **Anmelden → Schrank im Dropdown wählen → Speichern → Dienst installieren → Dienst starten.**
4. Im **Ausgabeordner** prüfen, ob die echten Originaldateien (z. B. `*.eml`/`*.msg`) ankommen.

---

## 🔐 Authentifizierung – Cloud & On-Premise

Einstellung **`AuthMode`**: `Auto` (Standard) · `Cookie` · `Token`.

### Token-Login (DocuWare Identity Service) — meist **Cloud** / modernes On-Prem 7.x
1. `GET {Server}/DocuWare/Platform/Home/IdentityServiceInfo`
   _(Fallback `…/Account/IdentityServiceInfo`)_ → URL des Identity Service
2. `GET {IdentityServiceUrl}/.well-known/openid-configuration` → `token_endpoint`
3. `POST {token_endpoint}` (Resource Owner Password Grant):

   | Feld | Wert |
   |------|------|
   | `grant_type` | `password` |
   | `scope` | `docuware.platform offline_access` |
   | `client_id` | `docuware.platform.net.client` |
   | `username` / `password` | Anmeldedaten |
   | `acr_values` | `organization:{Organization}` *(falls Organisation gesetzt)* |
4. `access_token` → `Authorization: Bearer …` an allen Requests; Erneuerung über `refresh_token`.

> ✅ **Verifiziert** gegen die offizielle DocuWare-Dokumentation
> (KBA-37505 *„How to switch from Cookie Authentication to OAuth2"* und
> developer.docuware.com *„OAuth Support"*).
> ⚠️ Die **Organisations-Übergabe** ist setup-abhängig: In der Cloud ist die
> Login-Domain bereits organisationsspezifisch (Feld kann leer bleiben); bei
> mehreren Organisationen wird `acr_values=organization:<Name>` gesetzt – bitte
> gegen die konkrete Instanz prüfen.

### Cookie-Login (klassisch) — v. a. **On-Premise**
`POST {Server}/DocuWare/Platform/Account/Logon` mit `UserName`, `Password`,
`Organization`, `RememberMe=false`. Cookies werden über einen `CookieContainer`
an alle Folge-Requests gehängt.

### `AuthMode=Auto`
Zuerst **Token**, bei Fehlschlag automatisch **Cookie**. Bei `HTTP 401` während
des Laufs: neu anmelden und Request wiederholen.

| Umgebung | Empfehlung |
|----------|------------|
| **DocuWare Cloud** | i. d. R. **Token** |
| **On-Premise** | oft **Cookie** |
| **Unsicher?** | **`Auto`** |

---

## ⚙️ Einstellungen (`config.json`)

Liegt neben der EXE; **GUI und Dienst lesen dieselbe Datei.** Alle Werte sind in
der GUI editierbar und mit Platzhaltern vorbelegt.

| Schlüssel | Bedeutung | Vorbelegung |
|-----------|-----------|-------------|
| `Server` | Basis-URL der DocuWare-Instanz | `https://IHR-SERVER.docuware.cloud` |
| `Organization` | Organisationsname | `IHRE-ORG` |
| `User` / `Password` | API-Anmeldedaten | `api-user` / `GEHEIM` |
| `AuthMode` | `Auto` / `Cookie` / `Token` | `Auto` |
| `FileCabinetId` | Ziel-Aktenschrank (per Dropdown gewählt) | `""` |
| `OutputRoot` | Zielordner für Exporte | `C:\Export\DocuWare` |
| `StateDbPath` | SQLite-Statusdatenbank | `C:\Export\DocuWare\export-state.db` |
| `DateFieldName` | Indexfeld für `Jahr/Monat`-Ordner (leer = aus) | `""` |
| `FolderHashDepth` | Hash-Unterordner: `0` aus · `1`=16 · `2`=256 | `0` |
| `PageSize` | Dokumente pro API-Seite | `500` |
| `DelayMs` | Pause zwischen Downloads (ms) | `100` |
| `MaxRetries` | Wiederholungen bei `429`/`5xx` | `4` |
| `DownloadPerSection` | pro Sektion statt Gesamtdatei | `false` |
| `RescanIntervalMinutes` | `0`=einmal · `>0`=periodisch | `0` |

<details>
<summary>Beispiel <code>config.json</code></summary>

```json
{
  "Server": "https://ihr-server.docuware.cloud",
  "Organization": "MEINE-ORG",
  "User": "api-user",
  "Password": "GEHEIM",
  "AuthMode": "Auto",
  "FileCabinetId": "a1b2c3d4-....",
  "OutputRoot": "C:\\Export\\DocuWare",
  "StateDbPath": "C:\\Export\\DocuWare\\export-state.db",
  "DateFieldName": "DOCUMENT_DATE",
  "FolderHashDepth": 0,
  "PageSize": 500,
  "DelayMs": 100,
  "MaxRetries": 4,
  "DownloadPerSection": false,
  "RescanIntervalMinutes": 0
}
```
</details>

---

## 🖱️ GUI-Bedienung

| Schaltfläche | Funktion |
|--------------|----------|
| **Anmelden / Schränke laden** | Meldet gemäß `AuthMode` an und füllt das Dropdown mit allen **Aktenschränken** (Baskets ausgeschlossen) – keine ID nötig |
| **Indexfelder laden** | Feldnamen des gewählten Schranks ins **Datumsfeld**-Dropdown (leer = keine Datumsordner) |
| **Verbindung testen** | Anmeldung + Anzahl Dokumente des Schranks |
| **Speichern** | Schreibt `config.json` |
| **Dienst installieren / starten / stoppen / deinstallieren** | Steuert den Windows-Dienst (vor *Installieren*/*Starten* wird automatisch gespeichert) |

Ein Timer zeigt **Dienststatus** (Stopped/Running/…), den **Fortschritt** (Anzahl
`done`) und die letzten Fehler aus der SQLite-DB.

---

## 🔧 Export-Logik (Dienst)

```
Anmelden (Token/Cookie)
   └─ Dokumente seitenweise:
         GET /FileCabinets/{fc}/Documents?start={n}&count={PageSize}&calculateTotalCount=true
      └─ pro Dokument (sofern noch nicht 'done'):
            GET /FileCabinets/{fc}/Documents/{id}/FileDownload?targetFileType=Auto&keepAnnotations=false
            → Dateiname aus Content-Disposition
            → schreibe *.part → umbenennen → Status 'done' in SQLite
```

- **Ordneraufteilung:** `DateFieldName` gesetzt & parsebar → `OutputRoot\JJJJ\MM\`,
  sonst flach (nicht parsebar → `_unsortiert`); optional Hash-Unterordner.
  Dateiname `{DocId}_{Originalname}` (ungültige Zeichen ersetzt, Länge begrenzt).
- **Pro Sektion** (optional): `/Documents/{id}/Sections` + `/Sections/{sid}/Data`,
  je Sektion eine Datei mit Suffix `_sNN`.
- **Robustheit:** Retry+Backoff bei `429`/`5xx`, Neuanmeldung bei `401`, `DelayMs`
  zwischen Downloads, sauberes `CancellationToken`-Handling.

---

## 🧱 Systemvoraussetzungen

- **Windows 10/11** bzw. Windows Server (für die fertige EXE)
- **Bauen:** .NET 8 SDK **auf Windows** (WinForms benötigt das Windows-Desktop-SDK;
  auf Linux/macOS lässt sich `net8.0-windows` nicht bauen)
- **Administratorrechte** zur Laufzeit (Manifest `requireAdministrator`) für die Dienstverwaltung

---

## 📁 Projektstruktur

| Datei | Inhalt |
|-------|--------|
| `Program.cs` | Einstieg: `--service` → Generic Host (Windows-Dienst), sonst GUI |
| `MainForm.cs` / `.Designer.cs` | WinForms-Oberfläche und Logik |
| `ExporterOptions.cs` | Laden/Speichern der `config.json` |
| `DocuWareClient.cs` | REST-Client: Token-/Cookie-Auth, Schränke, Felder, Seiten, Download, Sektionen |
| `ExportStateStore.cs` | SQLite-Statusspeicher (WAL) |
| `Worker.cs` | Hintergrunddienst mit Export-Logik |
| `ServiceManager.cs` | Dienst install/start/stop/delete (`sc.exe` + `ServiceController`) |
| `app.manifest` | `requireAdministrator` |

---

## 🗺️ Roadmap / mögliche Erweiterungen

- [ ] Passwort verschlüsselt speichern (Windows DPAPI)
- [ ] OAuth über App-Registrierung (Client Credentials) als Alternative zum Passwort-Grant
- [ ] Downloads streamen (große Dateien nicht komplett in den RAM)
- [ ] `Retry-After`-Header bei `429` respektieren
- [ ] Inkrementeller Export (nur neue/geänderte Dokumente)
- [ ] Parallele Downloads mit Drossel
- [ ] Metadaten-Sidecar (`{DocId}.json`/CSV) je Dokument
- [ ] Datei-Log zusätzlich zum EventLog
- [ ] Unit-Tests + GitHub-Actions-Windows-Build + signierte Release-Artefakte/Installer

---

## ❓ FAQ

**Kommen wirklich die Originaldateien an?**
Ja. Durch `targetFileType=Auto` liefert DocuWare das Originaldokument, die Endung
stammt aus `Content-Disposition`. Beim Mailarchiv erhältst du also echte
`*.eml`/`*.msg`, die sich im Mailclient öffnen lassen.

**Kann ich den Export gefahrlos abbrechen und fortsetzen?**
Ja. Bereits exportierte Dokumente sind in der SQLite-DB als `done` markiert und
werden übersprungen; geschrieben wird erst `*.part`, dann umbenannt.

**Muss ich die FileCabinet-ID kennen?**
Nein – nach dem Anmelden wählst du den Schrank einfach im Dropdown.

---

## 📜 Lizenz

Vorgeschlagen: **MIT** – lege dazu eine `LICENSE`-Datei im Repo an. _(Noch nicht
final festgelegt.)_

---

<div align="center">
<sub>Erstellt für den Export von DocuWare-Archiven · Platform REST API · .NET 8</sub>
</div>
