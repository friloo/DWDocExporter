using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DwDocExport;

/// <summary>Authentifizierungsmodi. Auto: zuerst Token, dann Cookie.</summary>
public enum AuthMode { Auto = 0, Cookie = 1, Token = 2 }

/// <summary>Vergleichsoperator einer Filterbedingung auf einem Indexfeld.</summary>
public enum FilterOperator { Equals = 0, NotEquals = 1, Contains = 2, StartsWith = 3 }

/// <summary>
/// Auswahl, welche der (zur Endung passenden) Sektionen gespeichert werden.
/// Greift nur, wenn mehr als eine passende Sektion existiert – sonst bleibt
/// die eine Sektion immer erhalten (kein Datenverlust).
/// Reihenfolge entspricht dem Dropdown auf Tab 3.
/// </summary>
public enum SectionSelection
{
    Alle = 0,               // alle passenden Sektionen
    ErsteUeberspringen = 1, // erste weglassen (z. B. Journal-Umschlag), Rest behalten
    NurLetzte = 2,          // nur die letzte passende Sektion (= eigentliche Mail)
    NurErste = 3            // nur die erste passende Sektion
}

/// <summary>Eine Filterbedingung: Feld OP Wert (clientseitig ausgewertet).</summary>
public sealed class FieldCondition
{
    [DisplayName("Feld")]
    public string Field { get; set; } = "";

    [DisplayName("Operator")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FilterOperator Operator { get; set; } = FilterOperator.Equals;

    [DisplayName("Wert")]
    public string Value { get; set; } = "";

    public override string ToString() => $"{Field} {Operator} {Value}";
}

/// <summary>
/// Alle Einstellungen des Exporters (config.json). Die ComponentModel-Attribute
/// erlauben die automatische, kategorisierte Anzeige in einem PropertyGrid.
/// Geheime Werte werden per DPAPI verschlüsselt gespeichert.
/// </summary>
public sealed class ExporterOptions
{
    // --- 01 Verbindung ---
    [Category("01 Verbindung"), DisplayName("Server"), Description("Basis-URL der DocuWare-Instanz, z. B. https://server.docuware.cloud")]
    public string Server { get; set; } = "https://IHR-SERVER.docuware.cloud";

    [Category("01 Verbindung"), DisplayName("Organisation")]
    public string Organization { get; set; } = "IHRE-ORG";

    [Category("01 Verbindung"), DisplayName("Benutzer")]
    public string User { get; set; } = "api-user";

    [Category("01 Verbindung"), DisplayName("Passwort"), PasswordPropertyText(true)]
    public string Password { get; set; } = "GEHEIM";

    [Category("01 Verbindung"), DisplayName("Authentifizierung"), Description("Token: Identity Service (Standard, empfohlen). Auto: zuerst Token, dann Cookie. Cookie wird von aktuellen DocuWare-Versionen nicht mehr unterstützt.")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthMode AuthMode { get; set; } = AuthMode.Token;

    [Category("01 Verbindung"), DisplayName("OAuth Client-ID"), Description("Leer = docuware.platform.net.client")]
    public string OAuthClientId { get; set; } = "";

    [Category("01 Verbindung"), DisplayName("OAuth Client-Secret"), Description("Gesetzt = Client-Credentials-Grant (App-Registrierung)."), PasswordPropertyText(true)]
    public string OAuthClientSecret { get; set; } = "";

    // --- 02 Netzwerk / TLS ---
    [Category("02 Netzwerk/TLS"), DisplayName("Proxy-URL")]
    public string ProxyUrl { get; set; } = "";

    [Category("02 Netzwerk/TLS"), DisplayName("Zertifikatsfehler ignorieren"), Description("Nur für Testsysteme!")]
    public bool IgnoreCertErrors { get; set; } = false;

    [Category("02 Netzwerk/TLS"), DisplayName("Eigenes CA-Zertifikat (Pfad)")]
    public string CustomCaPath { get; set; } = "";

    // --- 03 Ziel & Ablage ---
    [Category("03 Ziel & Ablage"), DisplayName("Archiv-ID"), Description("Wird i. d. R. über das Dropdown gewählt.")]
    public string FileCabinetId { get; set; } = "";

    [Category("03 Ziel & Ablage"), DisplayName("Ausgabeordner")]
    public string OutputRoot { get; set; } = @"C:\Export\DocuWare";

    [Category("03 Ziel & Ablage"), DisplayName("Status-DB (SQLite)")]
    public string StateDbPath { get; set; } = @"C:\Export\DocuWare\export-state.db";

    [Category("03 Ziel & Ablage"), DisplayName("Logdatei"), Description("Leer = OutputRoot\\dwdocexport.log")]
    public string LogFilePath { get; set; } = "";

    // --- 04 Ordner & Dateinamen ---
    [Category("04 Ordner & Dateinamen"), DisplayName("Datumsfeld"), Description("Indexfeld für Jahr/Monat-Ordner (leer = aus).")]
    public string DateFieldName { get; set; } = "";

    [Category("04 Ordner & Dateinamen"), DisplayName("Hash-Unterordner"), Description("0 = aus, 1 = 16, 2 = 256.")]
    public int FolderHashDepth { get; set; } = 0;

    [Category("04 Ordner & Dateinamen"), DisplayName("Pfad-Vorlage"), Description(@"z. B. {Feld:KUNDE}\{yyyy}\{MM} — überschreibt die Datumslogik.")]
    public string PathTemplate { get; set; } = "";

    [Category("04 Ordner & Dateinamen"), DisplayName("Dateinamen-Vorlage"), Description("Platzhalter: {DocId} {Original} {Ext} {Feld:NAME} {yyyy} …")]
    public string FileNameTemplate { get; set; } = "{DocId}_{Original}";

    [Category("04 Ordner & Dateinamen"), DisplayName("Datei-Datum aus Indexfeld setzen")]
    public bool SetFileDateFromField { get; set; } = false;

    // --- 05 Download ---
    [Category("05 Download"), DisplayName("Zielformat"), Description("Gültig: Auto, PDF, PDFA. Auto liefert das Originalformat (z. B. EML/MSG/PDF). 'Original' ist KEIN gültiger Wert!")]
    [TypeConverter(typeof(TargetFileTypeConverter))]
    public string TargetFileType { get; set; } = "Auto";

    [Category("05 Download"), DisplayName("Annotationen einbrennen")]
    public bool KeepAnnotations { get; set; } = false;

    [Category("05 Download"), DisplayName("Pro Sektion herunterladen")]
    public bool DownloadPerSection { get; set; } = false;

    [Category("05 Download"), DisplayName("Nur Sektionen mit Endung"),
     Description("z. B. eml oder eml,msg — lädt nur passende Sektionen einzeln (kein ZIP). Leer = alle.")]
    public string SectionExtensionFilter { get; set; } = "";

    [Category("05 Download"), DisplayName("Sektions-Auswahl"),
     Description("Bei mehreren passenden Sektionen: welche gespeichert werden. 'Erste überspringen' entfernt z. B. den Journal-Umschlag. Bei nur einer Sektion bleibt diese immer erhalten.")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SectionSelection SectionSelection { get; set; } = SectionSelection.ErsteUeberspringen;

    // --- 06 Leistung ---
    [Category("06 Leistung"), DisplayName("Seitengröße")]
    public int PageSize { get; set; } = 500;

    [Category("06 Leistung"), DisplayName("Verzögerung (ms)")]
    public int DelayMs { get; set; } = 100;

    [Category("06 Leistung"), DisplayName("Max. Wiederholungen"), Description("Wiederholungen bei Drosselung (429)/Serverfehlern. Für sehr große Läufe ruhig höher.")]
    public int MaxRetries { get; set; } = 6;

    [Category("06 Leistung"), DisplayName("Parallele Downloads")]
    public int MaxParallelDownloads { get; set; } = 4;

    [Category("06 Leistung"), DisplayName("Bandbreite (Byte/s)"), Description("0 = unbegrenzt.")]
    public long MaxBytesPerSecond { get; set; } = 0;

    [Category("06 Leistung"), DisplayName("Max. Dokumente pro Lauf"), Description("0 = alle. Sonst werden pro Lauf höchstens so viele (noch offene) Dokumente verarbeitet, z. B. 50.")]
    public int MaxDocumentsPerRun { get; set; } = 0;

    // --- 07 Integrität ---
    [Category("07 Integrität"), DisplayName("SHA-256 berechnen")]
    public bool ComputeSha256 { get; set; } = false;

    [Category("07 Integrität"), DisplayName("Metadaten-Sidecar schreiben")]
    public bool WriteMetadataSidecar { get; set; } = false;

    [Category("07 Integrität"), DisplayName("Manifest (CSV) schreiben")]
    public bool WriteManifestCsv { get; set; } = false;

    // --- 08 Filter ---
    [Category("08 Filter"), DisplayName("Filter-Datumsfeld")]
    public string FilterDateField { get; set; } = "";

    [Category("08 Filter"), DisplayName("Datum von"), Description("ISO oder TT.MM.JJJJ, leer = aus.")]
    public string FilterDateFrom { get; set; } = "";

    [Category("08 Filter"), DisplayName("Datum bis")]
    public string FilterDateTo { get; set; } = "";

    [Category("08 Filter"), DisplayName("Feldbedingungen")]
    public List<FieldCondition> FieldConditions { get; set; } = new();

    // --- 09 Nachscannen / Zeitplan ---
    [Category("09 Nachscannen/Zeitplan"), DisplayName("Inkrementell")]
    public bool Incremental { get; set; } = false;

    [Category("09 Nachscannen/Zeitplan"), DisplayName("Nachscannen (Min)"), Description("0 = einmal, > 0 = periodisch.")]
    public int RescanIntervalMinutes { get; set; } = 0;

    [Category("09 Nachscannen/Zeitplan"), DisplayName("Aktiv ab (HH:mm)")]
    public string ActiveFrom { get; set; } = "";

    [Category("09 Nachscannen/Zeitplan"), DisplayName("Aktiv bis (HH:mm)")]
    public string ActiveTo { get; set; } = "";

    // --- 10 Benachrichtigung ---
    [Category("10 Benachrichtigung"), DisplayName("Bei Abschluss benachrichtigen")]
    public bool NotifyOnComplete { get; set; } = false;

    [Category("10 Benachrichtigung"), DisplayName("Bei Fehler benachrichtigen")]
    public bool NotifyOnError { get; set; } = false;

    [Category("10 Benachrichtigung"), DisplayName("SMTP-Host")]
    public string SmtpHost { get; set; } = "";

    [Category("10 Benachrichtigung"), DisplayName("SMTP-Port")]
    public int SmtpPort { get; set; } = 587;

    [Category("10 Benachrichtigung"), DisplayName("SMTP SSL/TLS")]
    public bool SmtpUseSsl { get; set; } = true;

    [Category("10 Benachrichtigung"), DisplayName("SMTP-Benutzer")]
    public string SmtpUser { get; set; } = "";

    [Category("10 Benachrichtigung"), DisplayName("SMTP-Passwort"), PasswordPropertyText(true)]
    public string SmtpPassword { get; set; } = "";

    [Category("10 Benachrichtigung"), DisplayName("Absender (From)")]
    public string SmtpFrom { get; set; } = "";

    [Category("10 Benachrichtigung"), DisplayName("Empfänger (To)")]
    public string SmtpTo { get; set; } = "";

    [Category("10 Benachrichtigung"), DisplayName("Webhook-URL"), Description("Teams/Slack-kompatibel ({\"text\":…}).")]
    public string WebhookUrl { get; set; } = "";

    // --- 11 Dienst-Konto ---
    [Category("11 Dienst-Konto"), DisplayName("Konto"), Description("z. B. DOMAIN\\benutzer oder gMSA DOMAIN\\svc$")]
    public string ServiceAccount { get; set; } = "";

    [Category("11 Dienst-Konto"), DisplayName("Passwort"), PasswordPropertyText(true)]
    public string ServicePassword { get; set; } = "";

    // --- 12 Logging ---
    [Category("12 Logging"), DisplayName("Min. Loglevel"), Description("Trace/Debug/Information/Warning/Error.")]
    [TypeConverter(typeof(LogLevelConverter))]
    public string MinLogLevel { get; set; } = "Information";

    [Category("12 Logging"), DisplayName("Max. Logdateigröße (MB)")]
    public int LogMaxSizeMb { get; set; } = 10;

    // --- 13 Oberfläche ---
    [Category("13 Oberfläche"), DisplayName("Sprache"), Description("de oder en.")]
    [TypeConverter(typeof(LanguageConverter))]
    public string Language { get; set; } = "de";

    [Category("13 Oberfläche"), DisplayName("Dunkles Design")]
    public bool DarkMode { get; set; } = false;

    public const string DefaultFileName = "config.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    [Browsable(false)]
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, DefaultFileName);

    [Browsable(false)]
    public string EffectiveLogPath =>
        string.IsNullOrWhiteSpace(LogFilePath)
            ? Path.Combine(string.IsNullOrWhiteSpace(OutputRoot) ? AppContext.BaseDirectory : OutputRoot, "dwdocexport.log")
            : LogFilePath;

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
                    opts.Password = SecretProtector.Unprotect(opts.Password);
                    opts.OAuthClientSecret = SecretProtector.Unprotect(opts.OAuthClientSecret);
                    opts.SmtpPassword = SecretProtector.Unprotect(opts.SmtpPassword);
                    opts.ServicePassword = SecretProtector.Unprotect(opts.ServicePassword);
                    opts.FieldConditions ??= new();
                    return opts;
                }
            }
        }
        catch { }
        return new ExporterOptions();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var toWrite = (ExporterOptions)MemberwiseClone();
        toWrite.Password = SecretProtector.Protect(Password);
        toWrite.OAuthClientSecret = SecretProtector.Protect(OAuthClientSecret);
        toWrite.SmtpPassword = SecretProtector.Protect(SmtpPassword);
        toWrite.ServicePassword = SecretProtector.Protect(ServicePassword);

        File.WriteAllText(path, JsonSerializer.Serialize(toWrite, JsonOpts));
    }

    public ExporterOptions Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var copy = JsonSerializer.Deserialize<ExporterOptions>(json, JsonOpts)!;
        copy.FieldConditions ??= new();
        return copy;
    }
}

/// <summary>
/// Basis für TypeConverter, die einer string-Eigenschaft eine feste Auswahlliste
/// (Dropdown) im PropertyGrid geben.
/// </summary>
public abstract class FixedValuesConverter : StringConverter
{
    protected abstract string[] Values { get; }
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => true;
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) => new(Values);
}

/// <summary>Auswahl für das DocuWare-Zielformat (gültige FileDownloadType-Werte).</summary>
public sealed class TargetFileTypeConverter : FixedValuesConverter
{
    protected override string[] Values => new[] { "Auto", "PDF", "PDFA" };
}

/// <summary>Auswahl für den minimalen Log-Level.</summary>
public sealed class LogLevelConverter : FixedValuesConverter
{
    protected override string[] Values => new[] { "Trace", "Debug", "Information", "Warning", "Error" };
}

/// <summary>Auswahl für die Oberflächensprache.</summary>
public sealed class LanguageConverter : FixedValuesConverter
{
    protected override string[] Values => new[] { "de", "en" };
}
