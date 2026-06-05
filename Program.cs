using System;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DwDocExport;

/// <summary>
/// Einstiegspunkt. Entscheidet anhand der Argumente zwischen GUI-Modus
/// (Standard) und Dienst-Modus (Argument "--service").
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Dienst-Modus: Wird als Windows-Dienst über "--service" gestartet.
        if (Array.Exists(args, a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)))
        {
            RunService(args);
            return 0;
        }

        // GUI-Modus (Standard): WinForms-Fenster anzeigen.
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// Startet den Generic Host als Windows-Dienst. Der ContentRoot wird auf
    /// das EXE-Verzeichnis gesetzt, damit config.json zuverlässig gefunden wird.
    /// </summary>
    private static void RunService(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            // Wichtig: Dienste starten i.d.R. in C:\Windows\System32 – daher BaseDirectory erzwingen.
            ContentRootPath = AppContext.BaseDirectory
        });

        // Als Windows-Dienst registrieren (Dienstname).
        builder.Services.AddWindowsService(o => o.ServiceName = "DwDocExport");

        // Logging in das Windows-Ereignisprotokoll unter der Quelle "DwDocExport".
        builder.Logging.AddEventLog(settings => settings.SourceName = "DwDocExport");
        // Zusätzlich in die Logdatei schreiben (Pfad wird im Worker konfiguriert).
        builder.Logging.AddProvider(new FileLoggerProvider());

        // Der eigentliche Hintergrunddienst.
        builder.Services.AddHostedService<Worker>();

        var host = builder.Build();
        host.Run();
    }
}
