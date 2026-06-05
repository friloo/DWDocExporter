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
/// gespeichert; GUI und Dienst lesen dieselbe Datei.
/// </summary>
public sealed class ExporterOptions
{
    // --- Verbindung / Anmeldung ---
    public string Server { get; set; } = "https://IHR-SERVER.docuware.cloud";
    public string Organization { get; set; } = "IHRE-ORG";
    public string User { get; set; } = "api-user";
    public string Password { get; set; } = "GEHEIM";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthMode AuthMode { get; set; } = AuthMode.Auto;

    // --- Export ---
    public string FileCabinetId { get; set; } = "";
    public string OutputRoot { get; set; } = @"C:\Export\DocuWare";
    public string StateDbPath { get; set; } = @"C:\Export\DocuWare\export-state.db";

    /// <summary>Indexfeld, dessen Datum die Jahr/Monat-Ordnerstruktur bestimmt. Leer = keine Datumsordner.</summary>
    public string DateFieldName { get; set; } = "";

    /// <summary>0 = aus, 1 = 16 Hash-Unterordner, 2 = 256 Hash-Unterordner.</summary>
    public int FolderHashDepth { get; set; } = 0;

    public int PageSize { get; set; } = 500;
    public int DelayMs { get; set; } = 100;
    public int MaxRetries { get; set; } = 4;

    /// <summary>Wenn true, werden Dokumente pro Sektion heruntergeladen statt als Gesamtdatei.</summary>
    public bool DownloadPerSection { get; set; } = false;

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

    /// <summary>
    /// Lädt die Konfiguration aus der angegebenen Datei. Existiert sie nicht,
    /// werden die vorbelegten Platzhalterwerte zurückgegeben.
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
                    return opts;
            }
        }
        catch
        {
            // Bei defekter Datei einfach Standardwerte verwenden – kein Absturz.
        }
        return new ExporterOptions();
    }

    /// <summary>Speichert die Konfiguration als JSON (Verzeichnis wird bei Bedarf erstellt).</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(path, json);
    }
}
