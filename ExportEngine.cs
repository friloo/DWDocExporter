using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DwDocExport;

/// <summary>Betriebsart der Engine.</summary>
public enum ExportMode
{
    Export,       // normaler Export
    DryRun,       // nur zählen/anzeigen, nichts herunterladen
    Verify,       // vorhandene Dateien gegen die DB prüfen
    RetryErrors   // nur fehlerhafte Dokumente erneut versuchen
}

/// <summary>Fortschrittsmeldung für die GUI.</summary>
public sealed record ExportProgress(
    int Done, int Errors, int? Total, string Phase, string? CurrentDoc,
    double DocsPerSec, TimeSpan Elapsed);

/// <summary>Ergebnis eines Laufs.</summary>
public sealed class ExportResult
{
    public int Exported { get; set; }
    public int Errors { get; set; }
    public int Skipped { get; set; }
    public int Verified { get; set; }
    public int VerifyFailed { get; set; }
    public bool Cancelled { get; set; }
    public string Summary { get; set; } = "";
}

/// <summary>
/// Kapselt die gesamte Export-Logik – wird sowohl vom Windows-Dienst als auch
/// von der GUI genutzt. Unterstützt Filter, Pfad-/Namensvorlagen, Prüfsummen,
/// Bandbreitenbremse, Datei-Zeitstempel, Verifikation und erneuten Versuch.
/// </summary>
public sealed class ExportEngine
{
    private readonly ExporterOptions _opt;
    private readonly Action<string>? _log;

    public ExportEngine(ExporterOptions opt, Action<string>? log = null)
    {
        _opt = opt;
        _log = log;
    }

    private void Log(string msg)
    {
        _log?.Invoke(msg);
        FileLog.Write(msg);
    }

    /// <summary>Prüft, ob jetzt im aktiven Zeitfenster (ActiveFrom–ActiveTo) liegt.</summary>
    public static bool IsWithinWindow(ExporterOptions opt, DateTime now)
    {
        if (!TimeSpan.TryParse(opt.ActiveFrom, out var from) ||
            !TimeSpan.TryParse(opt.ActiveTo, out var to))
            return true; // kein Fenster konfiguriert

        var t = now.TimeOfDay;
        if (from == to) return true;
        return from < to ? (t >= from && t < to) : (t >= from || t < to); // über Mitternacht
    }

    public async Task<ExportResult> RunAsync(ExportMode mode, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        FileLog.Configure(_opt.EffectiveLogPath, FileLog.ParseLevel(_opt.MinLogLevel), _opt.LogMaxSizeMb);

        try
        {
            var result = mode switch
            {
                ExportMode.Verify => await VerifyAsync(progress, ct).ConfigureAwait(false),
                ExportMode.RetryErrors => await ExportCoreAsync(ExportMode.RetryErrors, progress, ct).ConfigureAwait(false),
                ExportMode.DryRun => await ExportCoreAsync(ExportMode.DryRun, progress, ct).ConfigureAwait(false),
                _ => await ExportCoreAsync(ExportMode.Export, progress, ct).ConfigureAwait(false),
            };

            await SendNotificationsAsync(mode, result, ct).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new ExportResult { Cancelled = true, Summary = "Abgebrochen." };
        }
    }

    // =====================================================================
    //  Export / DryRun / RetryErrors
    // =====================================================================

    private async Task<ExportResult> ExportCoreAsync(ExportMode mode, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var result = new ExportResult();
        var sw = Stopwatch.StartNew();
        _exportedRef = 0;
        _errorRef = 0;

        using var store = new ExportStateStore(_opt.StateDbPath);
        using var client = new DocuWareClient(_opt, Log);

        await client.AuthenticateAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(_opt.OutputRoot);

        int? total = null;
        try { total = await client.GetDocumentCountAsync(_opt.FileCabinetId, ct).ConfigureAwait(false); }
        catch { /* optional */ }

        void Report(string phase, string? cur) =>
            progress?.Report(new ExportProgress(result.Exported, result.Errors, total, phase, cur,
                result.Exported / Math.Max(0.001, sw.Elapsed.TotalSeconds), sw.Elapsed));

        // Liste der zu verarbeitenden Dokumente bestimmen.
        IEnumerable<DwDocument> Source() => EnumerateDocuments(client, store, mode, ct);

        var maxParallel = Math.Max(1, _opt.MaxParallelDownloads);
        using var gate = new SemaphoreSlim(maxParallel);
        var tasks = new List<Task>();

        Report(mode == ExportMode.DryRun ? "Trockenlauf" : "Export", null);

        foreach (var doc in Source())
        {
            ct.ThrowIfCancellationRequested();

            if (mode == ExportMode.DryRun)
            {
                result.Exported++; // im Trockenlauf = „würde exportieren"
                if (result.Exported <= 10)
                    Log($"[Trockenlauf] {doc.DocId} → {PlannedDirectory(doc)}");
                Report("Trockenlauf", doc.DocId);
                continue;
            }

            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var fieldsJson = JsonSerializer.Serialize(doc.Fields);
                    try
                    {
                        var (path, sha) = await ExportDocumentAsync(client, doc, ct).ConfigureAwait(false);
                        if (_opt.WriteMetadataSidecar)
                            WriteSidecar(path, doc, sha);
                        store.MarkDone(doc.DocId, path, fieldsJson, sha);
                        Interlocked.Increment(ref _exportedRef);
                        result.Exported = _exportedRef;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Log($"FEHLER Doc {doc.DocId}: {ex.Message}");
                        store.MarkError(doc.DocId, ex.Message, fieldsJson);
                        Interlocked.Increment(ref _errorRef);
                        result.Errors = _errorRef;
                    }
                    Report("Export", doc.DocId);
                    if (_opt.DelayMs > 0)
                        await Task.Delay(_opt.DelayMs, ct).ConfigureAwait(false);
                }
                finally { gate.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        result.Exported = mode == ExportMode.DryRun ? result.Exported : _exportedRef;
        result.Errors = _errorRef;
        store.SetLastRunUtc(DateTime.UtcNow);

        if (_opt.WriteManifestCsv && mode != ExportMode.DryRun)
        {
            try
            {
                var manifest = Path.Combine(_opt.OutputRoot, "manifest.csv");
                store.ExportManifestCsv(manifest);
                Log($"Manifest geschrieben: {manifest}");
            }
            catch (Exception ex) { Log($"Manifest-Fehler: {ex.Message}"); }
        }

        result.Summary = mode == ExportMode.DryRun
            ? $"Trockenlauf: {result.Exported} Dokument(e) würden exportiert."
            : $"Fertig. Neu: {result.Exported}, Fehler: {result.Errors}, gesamt erledigt: {store.CountDone()}.";
        Log(result.Summary);
        Report("Fertig", null);
        return result;
    }

    // Thread-übergreifende Zähler (für Parallelbetrieb).
    private int _exportedRef;
    private int _errorRef;

    /// <summary>Liefert die zu verarbeitenden Dokumente je nach Modus (mit Filter/Skip).</summary>
    private IEnumerable<DwDocument> EnumerateDocuments(
        DocuWareClient client, ExportStateStore store, ExportMode mode, CancellationToken ct)
    {
        if (mode == ExportMode.RetryErrors)
        {
            var ids = store.GetErrorDocIds();
            store.ClearErrors();
            Log($"Erneuter Versuch für {ids.Count} fehlerhafte(s) Dokument(e).");
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                DwDocument? doc = null;
                try { doc = client.GetDocumentAsync(_opt.FileCabinetId, id, ct).GetAwaiter().GetResult(); }
                catch (Exception ex) { Log($"Konnte Dok {id} nicht laden: {ex.Message}"); }
                if (doc != null) yield return doc;
            }
            yield break;
        }

        var pageSize = Math.Max(1, _opt.PageSize);
        var nextUrl = client.BuildFirstPageUrl(_opt.FileCabinetId, pageSize);

        while (!ct.IsCancellationRequested && nextUrl != null)
        {
            var page = client.GetDocumentsAsync(nextUrl, ct).GetAwaiter().GetResult();
            if (page.Items.Count == 0)
                yield break;

            var matched = page.Items.Where(d => DocumentFilter.Matches(d.Fields, _opt)).ToList();
            var todo = matched.Where(d => !store.IsDone(d.DocId)).ToList();

            // Inkrementell: komplette Seite bereits erledigt -> Schluss.
            if (_opt.Incremental && matched.Count > 0 && todo.Count == 0)
                yield break;

            foreach (var d in todo)
                yield return d;

            nextUrl = page.NextUrl;
        }
    }

    /// <summary>Exportiert ein Dokument; liefert gespeicherten Pfad und (optional) SHA-256.</summary>
    private async Task<(string path, string? sha)> ExportDocumentAsync(
        DocuWareClient client, DwDocument doc, CancellationToken ct)
    {
        var extFilter = ParseExtensionFilter(_opt.SectionExtensionFilter);

        // Ein gesetzter Endungs-Filter erzwingt den Sektions-Download, da der
        // FileDownload-Endpunkt bei mehreren Sektionen ein ZIP zurückgibt.
        if (_opt.DownloadPerSection || extFilter.Count > 0)
        {
            var sections = await client.GetSectionsAsync(_opt.FileCabinetId, doc.DocId, ct).ConfigureAwait(false);
            string? lastPath = null;
            string? lastSha = null;
            var saved = 0;
            foreach (var sec in sections)
            {
                // Vorabfilter anhand der Metadaten (OriginalFileName) – spart Downloads.
                if (extFilter.Count > 0 && !string.IsNullOrEmpty(sec.FileName)
                    && !MatchesExtension(sec.FileName!, extFilter))
                    continue;

                using var dl = await client.OpenSectionDownloadAsync(_opt.FileCabinetId, sec.Id, ct).ConfigureAwait(false);

                // Falls keine Metadaten vorlagen: nach dem Content-Disposition-Namen prüfen.
                var effectiveName = !string.IsNullOrEmpty(sec.FileName) ? sec.FileName! : dl.FileName;
                if (extFilter.Count > 0 && !MatchesExtension(effectiveName, extFilter))
                    continue;

                // Bei aktivem Filter die erste Treffer-Datei ohne "_sNN"-Suffix speichern
                // (sauberer Name); weitere Treffer bekommen einen Suffix gegen Kollisionen.
                int? sectionArg = extFilter.Count > 0
                    ? (saved == 0 ? (int?)null : saved)
                    : saved;

                (lastPath, lastSha) = await SaveAsync(doc, dl, sectionArg, ct).ConfigureAwait(false);
                saved++;
            }

            if (extFilter.Count > 0)
            {
                // Mit aktivem Filter NICHT auf den (ZIP-)Gesamtdownload zurückfallen.
                if (saved == 0)
                {
                    var available = string.Join(", ",
                        sections.Select(s => s.FileName ?? s.ContentType ?? s.Id));
                    throw new InvalidOperationException(
                        $"Keine Sektion mit Endung {string.Join("/", extFilter)} gefunden. " +
                        $"Vorhandene Sektionen: {(available.Length > 0 ? available : "(keine)")}.");
                }
                return (lastPath!, lastSha);
            }

            if (lastPath == null)
            {
                using var dl = await client.OpenDocumentDownloadAsync(_opt.FileCabinetId, doc.DocId, ct).ConfigureAwait(false);
                (lastPath, lastSha) = await SaveAsync(doc, dl, null, ct).ConfigureAwait(false);
            }
            return (lastPath!, lastSha);
        }
        else
        {
            using var dl = await client.OpenDocumentDownloadAsync(_opt.FileCabinetId, doc.DocId, ct).ConfigureAwait(false);
            return await SaveAsync(doc, dl, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Zerlegt den Endungs-Filter ("eml,msg") in eine normalisierte Menge ("eml","msg").</summary>
    private static HashSet<string> ParseExtensionFilter(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return set;
        foreach (var part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            set.Add(part.TrimStart('.').Trim());
        return set;
    }

    private static bool MatchesExtension(string fileName, HashSet<string> extFilter)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        return ext.Length > 0 && extFilter.Contains(ext);
    }

    private async Task<(string path, string? sha)> SaveAsync(DwDocument doc, DownloadStream dl, int? section, CancellationToken ct)
    {
        var date = GetDocDate(doc);
        var dir = ResolveDirectory(doc, dl.FileName, date);
        Directory.CreateDirectory(PathRules.ToExtendedLengthPath(dir));

        var fileName = ResolveFileName(doc, dl.FileName, date, section);
        var finalPath = Path.Combine(dir, fileName);
        var sha = await WriteStreamAtomicAsync(finalPath, dl.Content, ct).ConfigureAwait(false);

        if (_opt.SetFileDateFromField && date.HasValue)
        {
            try { File.SetLastWriteTime(PathRules.ToExtendedLengthPath(finalPath), date.Value); } catch { }
        }
        return (finalPath, sha);
    }

    private DateTime? GetDocDate(DwDocument doc)
    {
        var field = _opt.DateFieldName;
        if (string.IsNullOrWhiteSpace(field))
            return null;
        doc.Fields.TryGetValue(field, out var raw);
        return PathRules.TryParseDate(raw, out var dt) ? dt : null;
    }

    private string ResolveDirectory(DwDocument doc, string originalName, DateTime? date)
    {
        if (!string.IsNullOrWhiteSpace(_opt.PathTemplate))
            return PathRules.ResolveTemplatedDirectory(_opt.OutputRoot, _opt.PathTemplate, doc.Fields, date, doc.DocId, originalName);

        // Klassisch: Jahr/Monat + optional Hash.
        return PathRules.ResolveTargetDirectory(
            _opt.OutputRoot, !string.IsNullOrWhiteSpace(_opt.DateFieldName),
            date?.ToString("o", CultureInfo.InvariantCulture), _opt.FolderHashDepth, doc.DocId);
    }

    private string ResolveFileName(DwDocument doc, string originalName, DateTime? date, int? section)
    {
        if (!string.IsNullOrWhiteSpace(_opt.FileNameTemplate) && _opt.FileNameTemplate != "{DocId}_{Original}")
            return PathRules.ResolveTemplatedFileName(_opt.FileNameTemplate, doc.Fields, date, doc.DocId, originalName, section);

        return PathRules.BuildFileName(doc.DocId, originalName, section);
    }

    private string PlannedDirectory(DwDocument doc)
    {
        var date = GetDocDate(doc);
        return ResolveDirectory(doc, "beispiel.dat", date);
    }

    /// <summary>Streamt atomar in eine *.part-Datei (mit Drossel/Hash) und benennt um.</summary>
    private async Task<string?> WriteStreamAtomicAsync(string finalPath, Stream source, CancellationToken ct)
    {
        var partPath = finalPath + ".part";
        var finalEx = PathRules.ToExtendedLengthPath(finalPath);
        var partEx = PathRules.ToExtendedLengthPath(partPath);

        string? sha = null;
        using var hasher = _opt.ComputeSha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

        await using (var fs = new FileStream(partEx, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long totalBytes = 0;
            var sw = Stopwatch.StartNew();
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hasher?.AppendData(buffer, 0, read);
                totalBytes += read;
                await ThrottleAsync(totalBytes, sw, ct).ConfigureAwait(false);
            }
        }

        if (hasher != null)
            sha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();

        if (File.Exists(finalEx))
            File.Delete(finalEx);
        File.Move(partEx, finalEx);
        return sha;
    }

    /// <summary>Bandbreitenbremse: verzögert, wenn die Soll-Rate überschritten wird.</summary>
    private async Task ThrottleAsync(long totalBytes, Stopwatch sw, CancellationToken ct)
    {
        var limit = _opt.MaxBytesPerSecond;
        if (limit <= 0) return;
        var expected = totalBytes / (double)limit; // Soll-Sekunden
        var actual = sw.Elapsed.TotalSeconds;
        if (expected > actual)
            await Task.Delay(TimeSpan.FromSeconds(expected - actual), ct).ConfigureAwait(false);
    }

    private static void WriteSidecar(string savedPath, DwDocument doc, string? sha)
    {
        var sidecarPath = savedPath + ".metadata.json";
        var payload = new
        {
            DocId = doc.DocId,
            ExportedUtc = DateTime.UtcNow.ToString("o"),
            FileName = Path.GetFileName(savedPath),
            Sha256 = sha,
            Fields = doc.Fields
        };
        File.WriteAllText(PathRules.ToExtendedLengthPath(sidecarPath),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    // =====================================================================
    //  Verifikation
    // =====================================================================

    private async Task<ExportResult> VerifyAsync(IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var result = new ExportResult();
        var sw = Stopwatch.StartNew();
        using var store = new ExportStateStore(_opt.StateDbPath);
        var entries = store.GetDone();
        Log($"Verifikation: {entries.Count} Eintrag/Einträge.");

        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = e.SavedPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(PathRules.ToExtendedLengthPath(path)))
            {
                result.VerifyFailed++;
                Log($"FEHLT: {e.DocId} ({path})");
            }
            else if (!string.IsNullOrEmpty(e.Sha256))
            {
                var actual = await ComputeFileSha256Async(path, ct).ConfigureAwait(false);
                if (string.Equals(actual, e.Sha256, StringComparison.OrdinalIgnoreCase))
                    result.Verified++;
                else
                {
                    result.VerifyFailed++;
                    Log($"PRÜFSUMME ABWEICHEND: {e.DocId}");
                }
            }
            else
            {
                result.Verified++; // Datei vorhanden, keine gespeicherte Prüfsumme
            }

            progress?.Report(new ExportProgress(result.Verified, result.VerifyFailed, entries.Count,
                "Verifikation", e.DocId, 0, sw.Elapsed));
        }

        result.Summary = $"Verifikation fertig: {result.Verified} ok, {result.VerifyFailed} Problem(e).";
        Log(result.Summary);
        return result;
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(PathRules.ToExtendedLengthPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // =====================================================================
    //  Benachrichtigungen
    // =====================================================================

    private async Task SendNotificationsAsync(ExportMode mode, ExportResult result, CancellationToken ct)
    {
        var hasError = result.Errors > 0 || result.VerifyFailed > 0;
        var shouldNotify = (_opt.NotifyOnComplete && !result.Cancelled) || (_opt.NotifyOnError && hasError);
        if (!shouldNotify)
            return;

        var subject = $"DwDocExport – {(hasError ? "mit Fehlern" : "abgeschlossen")} ({mode})";
        try { await Notifier.SendAsync(_opt, subject, result.Summary, ct).ConfigureAwait(false); }
        catch { }
    }
}
