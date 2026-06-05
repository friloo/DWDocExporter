using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DwDocExport;

/// <summary>
/// Mögliche Authentifizierungsmodi. <see cref="Auto"/> versucht zuerst Token
/// (Identity Service) und fällt bei Bedarf auf Cookie-Login zurück.
/// </summary>
public enum AuthMode
{
    Auto = 0,
    Cookie = 1,
    Token = 2
}

/// <summary>
/// Alle Einstellungen des Exporters. Werden als config.json neben der EXE
/// gespeichert; GUI und Dienst lesen dieselbe Datei. Geheime Werte (Passwort,
/// Client-Secret) werden beim Speichern per DPAPI verschlüsselt.
/// </summary>
public sealed class ExporterOptions
{
    // --- Verbindung / Anmeldung ---
    public string Server { get; set; } = "https://IHR-SERVER.docuware.cloud";
    public string Organization { get; set; } = "IHRE-ORG";
    public string User { get; set; } = "api-user";

    /// <summary>
    /// Passwort. Wird beim Speichern per DPAPI verschlüsselt (Präfix "DPAPI:").
    /// Klartext-Werte (z. B. handvergebene Platzhalter) werden ebenfalls akzeptiert.
    /// </summary>
    public string Password { get; set; } = "GEHEIM";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthMode AuthMode { get; set; } = AuthMode.Auto;

    // --- OAuth App-Registrierung (optional, Alternative zum Passwort-Grant) ---
    /// <summary>Eigene Client-ID. Leer = Standard "docuware.platform.net.client".</summary>
    public string OAuthClientId { get; set; } = "";

    /// <summary>
    /// Client-Secret einer DocuWare App-Registrierung. Wenn gesetzt, wird der
    /// Client-Credentials-Grant verwendet (statt Resource Owner Password).
    /// Wird beim Speichern per DPAPI verschlüsselt.
    /// </summary>
    public string OAuthClientSecret { get; set; } = "";

    // --- Export ---
    public string FileCabinetId { get; set; } = "";
    public string OutputRoot { get; set; } = @"C:\Export\DocuWare";
    public string StateDbPath { get; set; } = @"C:\Export\DocuWare\export-state.db";

    /// <summary>Optionaler Pfad der Logdatei. Leer = OutputRoot\dwdocexport.log.</summary>
    public string LogFilePath { get; set; } = "";

    /// <summary>Indexfeld, dessen Datum die Jahr/Monat-Ordnerstruktur bestimmt. Leer = keine Datumsordner.</summary>
    public string DateFieldName { get; set; } = "";

    /// <summary>0 = aus, 1 = 16 Hash-Unterordner, 2 = 256 Hash-Unterordner.</summary>
    public int FolderHashDepth { get; set; } = 0;

    public int PageSize { get; set; } = 500;
    public int DelayMs { get; set; } = 100;
    public int MaxRetries { get; set; } = 4;

    /// <summary>Maximale Anzahl gleichzeitiger Downloads (1 = sequenziell).</summary>
    public int MaxParallelDownloads { get; set; } = 4;

    /// <summary>Wenn true, werden Dokumente pro Sektion heruntergeladen statt als Gesamtdatei.</summary>
    public bool DownloadPerSection { get; set; } = false;

    /// <summary>Wenn true, wird neben jeder Datei eine {Name}.metadata.json mit den Indexfeldern abgelegt.</summary>
    public bool WriteMetadataSidecar { get; set; } = false;

    /// <summary>
    /// Inkrementeller Modus: Beim Nachscannen wird der Durchlauf abgebrochen,
    /// sobald eine komplette Seite bereits exportierter Dokumente erreicht ist.
    /// </summary>
    public bool Incremental { get; set; } = false;

    /// <summary>0 = einmaliger Export, &gt;0 = periodisches Nachscannen im Minutenabstand.</summary>
    public int RescanIntervalMinutes { get; set; } = 0;

    /// <summary>Standard-Dateiname der Konfiguration (neben der EXE).</summary>
    public const string DefaultFileName = "config.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>Liefert den Standardpfad der config.json neben der laufenden EXE.</summary>
    public static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, DefaultFileName);

    /// <summary>Effektiver Logdateipfad (LogFilePath oder Standard im OutputRoot).</summary>
    public string EffectiveLogPath =>
        string.IsNullOrWhiteSpace(LogFilePath)
            ? Path.Combine(string.IsNullOrWhiteSpace(OutputRoot) ? AppContext.BaseDirectory : OutputRoot, "dwdocexport.log")
            : LogFilePath;

    /// <summary>
    /// Lädt die Konfiguration aus der angegebenen Datei. Existiert sie nicht,
    /// werden die vorbelegten Platzhalterwerte zurückgegeben. Geheime Werte
    /// werden entschlüsselt.
    /// </summary>
    public static ExporterOptions Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var opts = JsonSerializer.Deserialize<ExporterOptions>(json, JsonOpts);
                if (opts != null)
                {
                    // Geheime Werte für die Laufzeit entschlüsseln.
                    opts.Password = SecretProtector.Unprotect(opts.Password);
                    opts.OAuthClientSecret = SecretProtector.Unprotect(opts.OAuthClientSecret);
                    return opts;
                }
            }
        }
        catch
        {
            // Bei defekter Datei einfach Standardwerte verwenden – kein Absturz.
        }
        return new ExporterOptions();
    }

    /// <summary>
    /// Speichert die Konfiguration als JSON (Verzeichnis wird bei Bedarf erstellt).
    /// Geheime Werte werden vor dem Schreiben per DPAPI verschlüsselt; das laufende
    /// Objekt behält die Klartextwerte im Speicher.
    /// </summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Auf einer Kopie verschlüsseln, damit die GUI weiterhin Klartext anzeigt.
        var toWrite = (ExporterOptions)MemberwiseClone();
        toWrite.Password = SecretProtector.Protect(Password);
        toWrite.OAuthClientSecret = SecretProtector.Protect(OAuthClientSecret);

        var json = JsonSerializer.Serialize(toWrite, JsonOpts);
        File.WriteAllText(path, json);
    }
}
