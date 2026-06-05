using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwDocExport;

/// <summary>
/// Der Hintergrunddienst (Windows Worker Service). Exportiert alle Dokumente
/// eines DocuWare-Archivs im Originalformat – dateityp-neutral, fortsetzbar,
/// optional parallel und inkrementell.
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
        var configPath = ExporterOptions.DefaultPath;
        var opt = ExporterOptions.Load(configPath);

        // Datei-Log konfigurieren (zusätzlich zum Ereignisprotokoll).
        FileLog.Configure(opt.EffectiveLogPath);

        _logger.LogInformation("DwDocExport-Dienst gestartet. Konfiguration: {Path}", configPath);
        FileLog.Write($"Dienst gestartet. Konfiguration: {configPath}");

        if (string.IsNullOrWhiteSpace(opt.FileCabinetId))
        {
            _logger.LogError("Kein FileCabinetId konfiguriert – Dienst beendet die Arbeit.");
            FileLog.Write("FEHLER: Kein FileCabinetId konfiguriert.");
            return;
        }

        try
        {
            var firstPass = true;
            do
            {
                // Im inkrementellen Modus nur ab dem zweiten Durchlauf früh abbrechen.
                await RunExportPassAsync(opt, incremental: opt.Incremental && !firstPass, stoppingToken)
                    .ConfigureAwait(false);
                firstPass = false;

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
            FileLog.Write("Dienst gestoppt (Abbruch angefordert).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler im Exportdienst.");
            FileLog.Write($"FEHLER (Dienst): {ex.Message}");
        }
    }

    /// <summary>Führt einen vollständigen Export-Durchlauf über alle Dokumente aus.</summary>
    private async Task RunExportPassAsync(ExporterOptions opt, bool incremental, CancellationToken ct)
    {
        using var store = new ExportStateStore(opt.StateDbPath);
        using var client = new DocuWareClient(opt, msg =>
        {
            _logger.LogInformation("{Msg}", msg);
            FileLog.Write(msg);
        });

        await client.AuthenticateAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Authentifizierung erfolgreich. Beginne Export aus Archiv {Fc}.", opt.FileCabinetId);

        Directory.CreateDirectory(opt.OutputRoot);

        var pageSize = Math.Max(1, opt.PageSize);
        var maxParallel = Math.Max(1, opt.MaxParallelDownloads);
        var total = 0;

        var nextUrl = client.BuildFirstPageUrl(opt.FileCabinetId, pageSize);

        while (!ct.IsCancellationRequested && nextUrl != null)
        {
            var page = await client.GetDocumentsAsync(nextUrl, ct).ConfigureAwait(false);
            if (page.Items.Count == 0)
                break;

            // Bereits erledigte Dokumente herausfiltern.
            var todo = page.Items.Where(d => !store.IsDone(d.DocId)).ToList();

            // Inkrementell: Wenn auf dieser Seite nichts mehr zu tun ist, sind wir
            // bei bereits exportierten Beständen angekommen -> Durchlauf beenden.
            if (incremental && todo.Count == 0)
            {
                _logger.LogInformation("Inkrementell: Seite vollständig vorhanden – Durchlauf beendet.");
                break;
            }

            total += await ProcessDocumentsAsync(client, opt, store, todo, maxParallel, ct).ConfigureAwait(false);

            // Folge-Link der Plattform nutzen (stabiles Blättern).
            nextUrl = page.NextUrl;
        }

        store.SetLastRunUtc(DateTime.UtcNow);

        var summary = $"Durchlauf beendet. Neu exportiert: {total}. " +
                      $"Gesamt erledigt: {store.CountDone()}, Fehler: {store.CountError()}.";
        _logger.LogInformation("{Summary}", summary);
        FileLog.Write(summary);
    }

    /// <summary>
    /// Exportiert die übergebenen Dokumente, gedrosselt auf maxParallel gleichzeitige
    /// Downloads. Liefert die Anzahl erfolgreich exportierter Dokumente.
    /// </summary>
    private async Task<int> ProcessDocumentsAsync(
        DocuWareClient client, ExporterOptions opt, ExportStateStore store,
        List<DwDocument> docs, int maxParallel, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(maxParallel);
        var succeeded = 0;
        var tasks = new List<Task>();

        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            await gate.WaitAsync(ct).ConfigureAwait(false);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var fieldsJson = JsonSerializer.Serialize(doc.Fields);
                    try
                    {
                        var savedPath = await ExportDocumentAsync(client, opt, doc, ct).ConfigureAwait(false);

                        if (opt.WriteMetadataSidecar)
                            WriteSidecar(savedPath, doc, fieldsJson);

                        store.MarkDone(doc.DocId, savedPath, fieldsJson);
                        Interlocked.Increment(ref succeeded);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Fehler beim Export von Dokument {Id}.", doc.DocId);
                        FileLog.Write($"FEHLER Doc {doc.DocId}: {ex.Message}");
                        store.MarkError(doc.DocId, ex.Message, fieldsJson);
                    }

                    if (opt.DelayMs > 0)
                        await Task.Delay(opt.DelayMs, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return succeeded;
    }

    /// <summary>
    /// Exportiert ein einzelnes Dokument (ganze Datei oder pro Sektion) und
    /// gibt den gespeicherten Pfad zurück. Der Download wird gestreamt.
    /// </summary>
    private async Task<string> ExportDocumentAsync(
        DocuWareClient client, ExporterOptions opt, DwDocument doc, CancellationToken ct)
    {
        var dateConfigured = !string.IsNullOrWhiteSpace(opt.DateFieldName);
        string? dateRaw = null;
        if (dateConfigured)
            doc.Fields.TryGetValue(opt.DateFieldName, out dateRaw);

        var targetDir = PathRules.ResolveTargetDirectory(
            opt.OutputRoot, dateConfigured, dateRaw, opt.FolderHashDepth, doc.DocId);
        Directory.CreateDirectory(PathRules.ToExtendedLengthPath(targetDir));

        if (opt.DownloadPerSection)
        {
            var sectionIds = await client.GetSectionIdsAsync(opt.FileCabinetId, doc.DocId, ct).ConfigureAwait(false);
            string? lastPath = null;
            var index = 0;
            foreach (var sid in sectionIds)
            {
                using var dl = await client.OpenSectionDownloadAsync(sid, ct).ConfigureAwait(false);
                var fileName = PathRules.BuildFileName(doc.DocId, dl.FileName, sectionSuffix: index);
                lastPath = await WriteStreamAtomicAsync(targetDir, fileName, dl.Content, ct).ConfigureAwait(false);
                index++;
            }

            if (lastPath == null)
            {
                // Keine Sektionen -> ganze Datei.
                using var dl = await client.OpenDocumentDownloadAsync(opt.FileCabinetId, doc.DocId, ct).ConfigureAwait(false);
                var fileName = PathRules.BuildFileName(doc.DocId, dl.FileName, null);
                lastPath = await WriteStreamAtomicAsync(targetDir, fileName, dl.Content, ct).ConfigureAwait(false);
            }
            return lastPath;
        }
        else
        {
            using var dl = await client.OpenDocumentDownloadAsync(opt.FileCabinetId, doc.DocId, ct).ConfigureAwait(false);
            var fileName = PathRules.BuildFileName(doc.DocId, dl.FileName, null);
            return await WriteStreamAtomicAsync(targetDir, fileName, dl.Content, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Streamt den Inhalt zuerst in eine *.part-Datei und benennt sie dann atomar
    /// um – so entstehen keine halben Dateien bei Abbruch. Unterstützt lange Pfade.
    /// </summary>
    private static async Task<string> WriteStreamAtomicAsync(
        string targetDir, string fileName, Stream source, CancellationToken ct)
    {
        var finalPath = Path.Combine(targetDir, fileName);
        var partPath = finalPath + ".part";

        var finalEx = PathRules.ToExtendedLengthPath(finalPath);
        var partEx = PathRules.ToExtendedLengthPath(partPath);

        await using (var fs = new FileStream(partEx, FileMode.Create, FileAccess.Write,
                         FileShare.None, 81920, useAsync: true))
        {
            await source.CopyToAsync(fs, 81920, ct).ConfigureAwait(false);
        }

        if (File.Exists(finalEx))
            File.Delete(finalEx);
        File.Move(partEx, finalEx);

        return finalPath;
    }

    /// <summary>Schreibt eine Metadaten-Sidecar-Datei (.metadata.json) neben das Dokument.</summary>
    private static void WriteSidecar(string savedPath, DwDocument doc, string fieldsJson)
    {
        var sidecarPath = savedPath + ".metadata.json";
        var payload = new
        {
            DocId = doc.DocId,
            ExportedUtc = DateTime.UtcNow.ToString("o"),
            FileName = Path.GetFileName(savedPath),
            Fields = doc.Fields
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(PathRules.ToExtendedLengthPath(sidecarPath), json);
    }
}
