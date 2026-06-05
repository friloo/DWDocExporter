# DwDocExport

Ein **.NET 8 Windows-Programm (C#)**, das Dokumente aus **DocuWare** über die
**DocuWare Platform REST API** vollständig im **Originalformat** exportiert –
dateityp-neutral. Funktioniert sowohl gegen **DocuWare CLOUD** als auch
**ON-PREMISE**.

Der Download erfolgt mit `targetFileType=Auto`, sodass die Plattform die
Originaldatei liefert; der Dateiname wird aus dem `Content-Disposition`-Header
übernommen. Nichts ist auf Mails fest verdrahtet – der erste Anwendungsfall ist
ein Mailarchiv (EML/MSG), es funktioniert aber mit jedem Dateityp.

## Zwei Modi in einer EXE

| Modus | Start | Funktion |
|-------|-------|----------|
| **GUI** (Standard) | `DwDocExport.exe` | WinForms-Fenster zum Konfigurieren, Testen und Steuern des Dienstes |
| **Dienst** | `DwDocExport.exe --service` | Windows Worker Service, der den Export ausführt |

GUI und Dienst lesen dieselbe **`config.json`** (liegt neben der EXE).

## Administratorrechte

Die EXE enthält ein Manifest mit `requireAdministrator`. Beim Start erscheint
einmal die UAC-Abfrage. Das ist nötig, damit der Windows-Dienst per Klick
installiert/gestartet/gestoppt werden kann (`sc.exe` / `ServiceController`).

## Authentifizierung – Cloud und On-Premise

Einstellung **`AuthMode`**: `Auto` (Standard) | `Cookie` | `Token`.

### Token-Login (DocuWare Identity Service) – i. d. R. **Cloud** und modernes On-Prem 7.x
1. `GET {Server}/DocuWare/Platform/Home/IdentityServiceInfo`
   (Fallback: `.../Account/IdentityServiceInfo`) → URL des Identity Service.
2. `GET {IdentityServiceUrl}/.well-known/openid-configuration` → `token_endpoint`.
3. `POST {token_endpoint}` (Resource Owner Password Grant) mit den Formularfeldern:
   - `grant_type=password`
   - `scope=docuware.platform offline_access`
   - `client_id=docuware.platform.net.client`
   - `username`, `password`
   - `acr_values=organization:{Organization}` (sofern eine Organisation angegeben ist)
4. Das `access_token` wird als `Authorization: Bearer …` an alle Platform-Requests
   gehängt; bei Ablauf wird über das `refresh_token` erneuert.

> **Hinweis zu den Token-Parametern:** `client_id`, `scope` und der Grant-Typ
> sind gegen die offizielle DocuWare-Dokumentation verifiziert
> (KBA-37505 „How to switch from Cookie Authentication to OAuth2“ sowie
> developer.docuware.com „OAuth Support“). Die Übergabe der **Organisation**
> ist je nach Setup unterschiedlich: In der Cloud ist die Login-Domain bereits
> organisationsspezifisch, sodass `Organization` oft leer bleiben kann. Bei
> mehreren Organisationen wird hier `acr_values=organization:<Name>` gesetzt –
> bitte gegen die konkrete DocuWare-Instanz prüfen und ggf. anpassen.

### Cookie-Login (klassisch) – v. a. **On-Premise**
- `POST {Server}/DocuWare/Platform/Account/Logon`
  mit den Formularfeldern `UserName`, `Password`, `Organization`,
  `RememberMe=false`. Der `HttpClient` verwendet einen `CookieContainer`; die
  erhaltenen Cookies werden an alle weiteren Requests gesendet.

### `AuthMode=Auto`
Es wird zuerst der **Token-Weg** versucht (IdentityServiceInfo abrufen). Schlägt
das fehl oder ist kein Identity Service vorhanden, wird automatisch auf
**Cookie-Login** zurückgefallen. Tritt während des Laufs ein `HTTP 401` auf, wird
neu authentifiziert und der Request wiederholt.

**Faustregel:** Cloud → meist **Token**, On-Premise → oft **Cookie**. `Auto` wählt
in der Regel das Richtige.

## Einstellungen (`config.json`)

Alle Werte sind in der GUI editierbar und mit Platzhaltern vorbelegt:

| Schlüssel | Bedeutung | Vorbelegung |
|-----------|-----------|-------------|
| `Server` | Basis-URL der DocuWare-Instanz | `https://IHR-SERVER.docuware.cloud` |
| `Organization` | Organisationsname | `IHRE-ORG` |
| `User` / `Password` | API-Anmeldedaten | `api-user` / `GEHEIM` |
| `AuthMode` | `Auto` / `Cookie` / `Token` | `Auto` |
| `FileCabinetId` | Ziel-Aktenschrank (per Dropdown gewählt) | `""` |
| `OutputRoot` | Zielordner für Exporte | `C:\Export\DocuWare` |
| `StateDbPath` | SQLite-Statusdatenbank | `C:\Export\DocuWare\export-state.db` |
| `DateFieldName` | Indexfeld für Jahr/Monat-Ordner (leer = aus) | `""` |
| `FolderHashDepth` | Hash-Unterordner: `0` aus, `1`=16, `2`=256 | `0` |
| `PageSize` | Dokumente pro API-Seite | `500` |
| `DelayMs` | Pause zwischen Downloads (ms) | `100` |
| `MaxRetries` | Wiederholungen bei 429/5xx | `4` |
| `DownloadPerSection` | pro Sektion statt Gesamtdatei | `false` |
| `RescanIntervalMinutes` | `0`=einmal, `>0`=periodisch nachscannen | `0` |

## GUI-Bedienung

1. **Anmelden / Schränke laden** – meldet gemäß `AuthMode` an und füllt das
   Dropdown mit allen **Aktenschränken** (Baskets ausgeschlossen). Man muss
   keine ID kennen – der Schrank wird einfach im Dropdown gewählt.
2. **Indexfelder laden** – füllt das Dropdown `Datumsfeld` mit den Feldnamen des
   gewählten Schranks (für die Ordnerstruktur nach Datum). Leer = keine
   Datumsordner.
3. **Verbindung testen** – meldet an und zeigt die Anzahl Dokumente des Schranks.
4. **Speichern** – schreibt `config.json`.
5. **Dienst installieren / starten / stoppen / deinstallieren** – steuert den
   Windows-Dienst. Vor *Installieren* und *Starten* wird automatisch gespeichert.
6. Ein Timer zeigt **Dienststatus** (Stopped/Running/…) sowie den **Fortschritt**
   (Anzahl `done`) und die letzten Fehler aus der SQLite-DB.

Empfohlener Ablauf: **Anmelden → Schrank im Dropdown wählen → (optional Datumsfeld)
→ Speichern → Dienst installieren → Dienst starten.**

## Export-Logik (Dienst)

- Authentifizierung wie oben (Token oder Cookie je `AuthMode`).
- Dokumente werden **seitenweise** geladen:
  `/FileCabinets/{fc}/Documents?start={start}&count={PageSize}&calculateTotalCount=true`.
- Download im **Originalformat**:
  `/FileCabinets/{fc}/Documents/{id}/FileDownload?targetFileType=Auto&keepAnnotations=false`;
  Dateiname/Endung aus `Content-Disposition`.
- Optional **pro Sektion**: `/Documents/{id}/Sections` und `/Sections/{sid}/Data`.
- **Keine Doppel-Downloads / fortsetzbar:** SQLite (WAL),
  Tabelle `Documents(DocId PK, Status, SavedPath, Fields, Error, UpdatedUtc)`.
  Vor jedem Download wird `IsDone` geprüft; geschrieben wird erst `*.part`, dann
  atomar umbenannt. Indexfelder werden als JSON in `Fields` abgelegt.
- **Ordneraufteilung:** Ist `DateFieldName` gesetzt und der Wert parsebar →
  `OutputRoot\JJJJ\MM\`, sonst flach in `OutputRoot` (nicht parsebar →
  `_unsortiert`). Optional `FolderHashDepth` für Hash-Unterordner. Dateiname:
  `{DocId}_{Originalname}`, ungültige Zeichen ersetzt, Länge begrenzt.
- **Robustheit:** Retry mit exponentiellem Backoff bei `429`/`5xx` (`MaxRetries`);
  bei `401` Neuanmeldung + Wiederholung; `DelayMs` zwischen Downloads;
  `RescanIntervalMinutes` (`0`=einmal, `>0`=periodisch); sauberes
  `CancellationToken`-Handling.
- **Logging** in das Windows-Ereignisprotokoll (Quelle `DwDocExport`).

## Bauen / Veröffentlichen

Voraussetzung: **.NET 8 SDK auf einem Windows-PC** (WinForms erfordert das
Windows-Desktop-SDK; auf Linux/macOS lässt sich das Projekt nicht bauen).

```powershell
# Einzelne, eigenständige EXE (win-x64, self-contained, single-file)
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Ergebnis: `bin\Release\net8.0-windows\win-x64\publish\DwDocExport.exe`.
Die EXE auf den Zielrechner kopieren und starten (GUI) bzw. als Dienst
installieren.

## Prüfen, ob echte Originaldateien ankommen

Nach einem Testlauf den `OutputRoot` öffnen und stichprobenartig prüfen, ob die
**Originaldateien** korrekt exportiert wurden – beim Mailarchiv also z. B. echte
`*.eml`- oder `*.msg`-Dateien, die sich in Outlook/einem Mailclient öffnen
lassen. Da `targetFileType=Auto` verwendet wird und der Name aus
`Content-Disposition` stammt, entspricht die Endung dem Originaldokument
(PDF, MSG, EML, DOCX, XLSX, …). Bei `DownloadPerSection=true` wird je Sektion
eine Datei mit Suffix `_sNN` erzeugt.

## Projektstruktur

| Datei | Inhalt |
|-------|--------|
| `Program.cs` | Einstieg: `--service` → Generic Host (Windows-Dienst), sonst GUI |
| `MainForm.cs` / `MainForm.Designer.cs` | WinForms-Oberfläche und Logik |
| `ExporterOptions.cs` | Laden/Speichern der `config.json` |
| `DocuWareClient.cs` | REST-Client mit Token- und Cookie-Auth, Schränke, Felder, Seiten, Download, Sektionen |
| `ExportStateStore.cs` | SQLite-Statusspeicher (WAL) |
| `Worker.cs` | Hintergrunddienst mit der Export-Logik |
| `ServiceManager.cs` | Dienst install/start/stop/delete (`sc.exe` + `ServiceController`) |
| `app.manifest` | `requireAdministrator` |
