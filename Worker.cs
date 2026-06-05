using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwDocExport;

/// <summary>
/// Der Hintergrunddienst (Windows Worker Service). Exportiert alle Dokumente
/// eines DocuWare-Schranks im Originalformat – dateityp-neutral, fortsetzbar.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Konfiguration neben der EXE laden.
        var configPath = ExporterOptions.DefaultPath;
        var opt = ExporterOptions.Load(configPath);

        _logger.LogInformation("DwDocExport-Dienst gestartet. Konfiguration: {Path}", configPath);

        if (string.IsNullOrWhiteSpace(opt.FileCabinetId))
        {
            _logger.LogError("Kein FileCabinetId konfiguriert – Dienst beendet die Arbeit.");
            return;
        }

        try
        {
            do
            {
                await RunExportPassAsync(opt, stoppingToken).ConfigureAwait(false);

                if (opt.RescanIntervalMinutes <= 0)
                {
                    _logger.LogInformation("Einmaliger Export abgeschlossen (RescanIntervalMinutes=0).");
                    break;
                }

                _logger.LogInformation("Warte {Min} Minuten bis zum nächsten Scan.", opt.RescanIntervalMinutes);
                await Task.Delay(TimeSpan.FromMinutes(opt.RescanIntervalMinutes), stoppingToken)
                    .ConfigureAwait(false);
            }
            while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Dienst wird gestoppt (Abbruch angefordert).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler im Exportdienst.");
        }
    }

    /// <summary>Führt einen vollständigen Export-Durchlauf über alle Dokumente aus.</summary>
    private async Task RunExportPassAsync(ExporterOptions opt, CancellationToken ct)
    {
        using var store = new ExportStateStore(opt.StateDbPath);
        using var client = new DocuWareClient(opt, msg => _logger.LogInformation("{Msg}", msg));

        await client.AuthenticateAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Authentifizierung erfolgreich. Beginne Export aus Schrank {Fc}.", opt.FileCabinetId);

        Directory.CreateDirectory(opt.OutputRoot);

        var start = 0;
        var pageSize = Math.Max(1, opt.PageSize);
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            var page = await client.GetPageAsync(opt.FileCabinetId, start, pageSize, ct).ConfigureAwait(false);
            if (page.Count == 0)
                break;

            foreach (var doc in page)
            {
                ct.ThrowIfCancellationRequested();

                if (store.IsDone(doc.DocId))
                    continue;

                var fieldsJson = JsonSerializer.Serialize(doc.Fields);
                try
                {
                    var savedPath = await ExportDocumentAsync(client, opt, doc, ct).ConfigureAwait(false);
                    store.MarkDone(doc.DocId, savedPath, fieldsJson);
                    total++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Fehler beim Export von Dokument {Id}.", doc.DocId);
                    store.MarkError(doc.DocId, ex.Message, fieldsJson);
                }

                if (opt.DelayMs > 0)
                    await Task.Delay(opt.DelayMs, ct).ConfigureAwait(false);
            }

            // Letzte Seite erreicht, wenn weniger als PageSize zurückkamen.
            if (page.Count < pageSize)
                break;

            start += pageSize;
        }

        _logger.LogInformation(
            "Durchlauf beendet. Neu exportiert: {New}. Gesamt erledigt: {Done}, Fehler: {Err}.",
            total, store.CountDone(), store.CountError());
    }

    /// <summary>
    /// Exportiert ein einzelnes Dokument (ganze Datei oder pro Sektion) und
    /// gibt den gespeicherten Pfad zurück.
    /// </summary>
    private async Task<string> ExportDocumentAsync(
        DocuWareClient client, ExporterOptions opt, DwDocument doc, CancellationToken ct)
    {
        var targetDir = BuildTargetDirectory(opt, doc);
        Directory.CreateDirectory(targetDir);

        if (opt.DownloadPerSection)
        {
            var sectionIds = await client.GetSectionIdsAsync(opt.FileCabinetId, doc.DocId, ct).ConfigureAwait(false);
            string? lastPath = null;
            var index = 0;
            foreach (var sid in sectionIds)
            {
                var dl = await client.DownloadSectionAsync(sid, ct).ConfigureAwait(false);
                var fileName = BuildFileName(doc.DocId, dl.FileName, sectionSuffix: index);
                lastPath = WriteAtomic(targetDir, fileName, dl.Data);
                index++;
            }

            if (lastPath == null)
            {
                // Keine Sektionen gefunden -> ganze Datei laden.
                var dl = await client.DownloadDocumentAsync(opt.FileCabinetId, doc.DocId, ct).ConfigureAwait(false);
                var fileName = BuildFileName(doc.DocId, dl.FileName, null);
                lastPath = WriteAtomic(targetDir, fileName, dl.Data);
            }
            return lastPath;
        }
        else
        {
            var dl = await client.DownloadDocumentAsync(opt.FileCabinetId, doc.DocId, ct).ConfigureAwait(false);
            var fileName = BuildFileName(doc.DocId, dl.FileName, null);
            return WriteAtomic(targetDir, fileName, dl.Data);
        }
    }

    /// <summary>
    /// Ermittelt das Zielverzeichnis: bei gesetztem und parsebarem Datumsfeld
    /// OutputRoot\JJJJ\MM, sonst flach (bzw. _unsortiert). Optional Hash-Unterordner.
    /// </summary>
    private static string BuildTargetDirectory(ExporterOptions opt, DwDocument doc)
    {
        var dir = opt.OutputRoot;

        if (!string.IsNullOrWhiteSpace(opt.DateFieldName))
        {
            if (doc.Fields.TryGetValue(opt.DateFieldName, out var raw) &&
                TryParseDate(raw, out var dt))
            {
                dir = Path.Combine(opt.OutputRoot,
                    dt.ToString("yyyy", CultureInfo.InvariantCulture),
                    dt.ToString("MM", CultureInfo.InvariantCulture));
            }
            else
            {
                // Datumsfeld vorgesehen, aber nicht parsebar -> Sammelordner.
                dir = Path.Combine(opt.OutputRoot, "_unsortiert");
            }
        }

        if (opt.FolderHashDepth > 0)
        {
            var hash = StableHashHex(doc.DocId);
            // Tiefe 1 -> 1 Hex-Zeichen (16 Ordner), Tiefe 2 -> 2 Hex-Zeichen (256 Ordner).
            var depth = Math.Min(opt.FolderHashDepth, hash.Length);
            for (var i = 0; i < depth; i++)
                dir = Path.Combine(dir, hash[i].ToString());
        }

        return dir;
    }

    /// <summary>Versucht, einen DocuWare-Datumswert in verschiedenen Formaten zu parsen.</summary>
    private static bool TryParseDate(string? raw, out DateTime dt)
    {
        dt = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // DocuWare liefert Datumswerte häufig als ISO-8601 oder /Date(…)/.
        if (raw.StartsWith("/Date(", StringComparison.Ordinal))
        {
            var inner = raw.Substring(6).TrimEnd(')', '/');
            var sign = inner.IndexOfAny(new[] { '+', '-' }, 1);
            if (sign > 0)
                inner = inner.Substring(0, sign);
            if (long.TryParse(inner, out var ms))
            {
                dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                return true;
            }
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal, out dt)
               || DateTime.TryParse(raw, CultureInfo.CurrentCulture,
                   DateTimeStyles.AssumeLocal, out dt);
    }

    /// <summary>
    /// Baut den Zieldateinamen "{DocId}_{Originalname}" mit bereinigten Zeichen
    /// und begrenzter Länge. Bei Sektionen wird ein Suffix eingefügt.
    /// </summary>
    private static string BuildFileName(string docId, string originalName, int? sectionSuffix)
    {
        var cleanOriginal = SanitizeFileName(originalName);
        var ext = Path.GetExtension(cleanOriginal);
        var baseName = Path.GetFileNameWithoutExtension(cleanOriginal);

        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "document";

        var sectionPart = sectionSuffix.HasValue ? $"_s{sectionSuffix.Value:00}" : "";
        var prefix = $"{SanitizeFileName(docId)}_";

        // Gesamtlänge des Dateinamens begrenzen (Windows-Pfadgrenzen beachten).
        const int maxBaseLen = 120;
        if (baseName.Length > maxBaseLen)
            baseName = baseName.Substring(0, maxBaseLen);

        return $"{prefix}{baseName}{sectionPart}{ext}";
    }

    /// <summary>Ersetzt für Dateinamen ungültige Zeichen durch Unterstriche.</summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "_";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Schreibt zuerst eine *.part-Datei und benennt sie dann atomar um – so
    /// entstehen keine halben Dateien bei Abbruch. Bestehende Datei wird ersetzt.
    /// </summary>
    private static string WriteAtomic(string targetDir, string fileName, byte[] data)
    {
        var finalPath = Path.Combine(targetDir, fileName);
        var partPath = finalPath + ".part";

        File.WriteAllBytes(partPath, data);
        if (File.Exists(finalPath))
            File.Delete(finalPath);
        File.Move(partPath, finalPath);

        return finalPath;
    }

    /// <summary>Stabiler Hex-Hash (für Hash-Unterordner) – plattformunabhängig deterministisch.</summary>
    private static string StableHashHex(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
