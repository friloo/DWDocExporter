using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwDocExport;

/// <summary>
/// Windows Worker Service. Lädt die Konfiguration, respektiert das Zeitfenster
/// und führt den Export über die gemeinsame <see cref="ExportEngine"/> aus –
/// einmalig oder periodisch (RescanIntervalMinutes).
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configPath = ExporterOptions.DefaultPath;
        var opt = ExporterOptions.Load(configPath);

        FileLog.Configure(opt.EffectiveLogPath, FileLog.ParseLevel(opt.MinLogLevel), opt.LogMaxSizeMb);
        _logger.LogInformation("DwDocExport-Dienst gestartet. Konfiguration: {Path}", configPath);
        FileLog.Write($"Dienst gestartet. Konfiguration: {configPath}");

        if (string.IsNullOrWhiteSpace(opt.FileCabinetId))
        {
            _logger.LogError("Kein FileCabinetId konfiguriert – Dienst beendet die Arbeit.");
            FileLog.Write("FEHLER: Kein Archiv (FileCabinetId) konfiguriert.");
            return;
        }

        try
        {
            var firstPass = true;
            do
            {
                await WaitForActiveWindowAsync(opt, stoppingToken).ConfigureAwait(false);

                // Alle Profile (config.json + profiles/*.json) nacheinander abarbeiten.
                foreach (var path in ProfileManager.GetAllConfigPaths())
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    var runOpt = ExporterOptions.Load(path);
                    if (string.IsNullOrWhiteSpace(runOpt.FileCabinetId))
                        continue;
                    runOpt.Incremental = runOpt.Incremental && !firstPass;

                    _logger.LogInformation("Starte Profil {Path}.", path);
                    var engine = new ExportEngine(runOpt, msg => _logger.LogInformation("{Msg}", msg));
                    var result = await engine.RunAsync(ExportMode.Export, null, stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation("{Summary}", result.Summary);
                }
                firstPass = false;

                if (opt.RescanIntervalMinutes <= 0)
                {
                    _logger.LogInformation("Einmaliger Export abgeschlossen.");
                    break;
                }

                _logger.LogInformation("Warte {Min} Minuten bis zum nächsten Scan.", opt.RescanIntervalMinutes);
                await Task.Delay(TimeSpan.FromMinutes(opt.RescanIntervalMinutes), stoppingToken).ConfigureAwait(false);
            }
            while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Dienst wird gestoppt.");
            FileLog.Write("Dienst gestoppt (Abbruch angefordert).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unerwarteter Fehler im Exportdienst.");
            FileLog.Write($"FEHLER (Dienst): {ex.Message}");
        }
    }

    /// <summary>Wartet, bis das konfigurierte Zeitfenster aktiv ist (max. 5-Minuten-Schritte).</summary>
    private async Task WaitForActiveWindowAsync(ExporterOptions opt, CancellationToken ct)
    {
        var logged = false;
        while (!ct.IsCancellationRequested && !ExportEngine.IsWithinWindow(opt, DateTime.Now))
        {
            if (!logged)
            {
                _logger.LogInformation("Außerhalb des aktiven Zeitfensters ({From}–{To}) – warte.",
                    opt.ActiveFrom, opt.ActiveTo);
                logged = true;
            }
            await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        }
    }
}
