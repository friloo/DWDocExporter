using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;

namespace DwDocExport;

/// <summary>
/// Verwaltet den Windows-Dienst "DwDocExport": Installieren (sc.exe), Starten,
/// Stoppen, Deinstallieren und Statusabfrage (ServiceController).
/// </summary>
public static class ServiceManager
{
    public const string ServiceName = "DwDocExport";
    public const string DisplayName = "DocuWare Dokument-Export";

    /// <summary>
    /// Installiert den Dienst mit Autostart und konfiguriert automatischen
    /// Neustart bei Fehlern. binPath verweist auf die EXE mit Argument --service.
    /// </summary>
    public static (bool ok, string output) Install(string exePath, string? account = null, string? password = null)
    {
        // Anführungszeichen um den Pfad; sc.exe erwartet "binPath= " mit Leerzeichen nach '='.
        var binPath = $"\"{exePath}\" --service";

        var args = $"create {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"";
        // Optionales Dienst-Konto (z. B. DOMAIN\\user oder gMSA „DOMAIN\\svc$").
        if (!string.IsNullOrWhiteSpace(account))
        {
            args += $" obj= \"{account}\"";
            if (!string.IsNullOrWhiteSpace(password))
                args += $" password= \"{password}\"";
        }

        var create = RunSc(args);
        if (!create.ok)
            return create;

        // Beschreibung setzen (optional, ignoriert Fehler).
        RunSc($"description {ServiceName} \"Exportiert DocuWare-Dokumente im Originalformat.\"");

        // Automatischer Neustart bei Absturz: nach 5s, 10s, 30s; Reset-Zähler nach 1 Tag.
        var failure = RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");

        return (true, create.output + Environment.NewLine + failure.output);
    }

    /// <summary>Deinstalliert den Dienst (zuvor stoppen).</summary>
    public static (bool ok, string output) Uninstall()
    {
        var stop = Stop();
        var del = RunSc($"delete {ServiceName}");
        return (del.ok, stop.output + Environment.NewLine + del.output);
    }

    /// <summary>Startet den Dienst über den ServiceController.</summary>
    public static (bool ok, string output) Start()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
                return (true, "Dienst läuft bereits.");

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return (true, "Dienst gestartet.");
        }
        catch (Exception ex)
        {
            return (false, $"Start fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>Stoppt den Dienst über den ServiceController.</summary>
    public static (bool ok, string output) Stop()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Stopped)
                return (true, "Dienst ist bereits gestoppt.");

            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            return (true, "Dienst gestoppt.");
        }
        catch (InvalidOperationException)
        {
            // Dienst existiert nicht – als "gestoppt" behandeln.
            return (true, "Dienst nicht vorhanden.");
        }
        catch (Exception ex)
        {
            return (false, $"Stopp fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>Liefert den aktuellen Dienststatus als Text (z. B. "Running", "Stopped", "Nicht installiert").</summary>
    public static string GetStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return "Nicht installiert";
        }
        catch (Exception ex)
        {
            return $"Unbekannt ({ex.Message})";
        }
    }

    /// <summary>Prüft, ob der Dienst installiert ist.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status; // Zugriff erzwingt Existenzprüfung.
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Führt sc.exe mit den angegebenen Argumenten aus und sammelt die Ausgabe.</summary>
    private static (bool ok, string output) RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null)
                return (false, "sc.exe konnte nicht gestartet werden.");

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);

            var text = ($"sc {arguments}\n{stdout}{stderr}").Trim();
            return (p.ExitCode == 0, text);
        }
        catch (Exception ex)
        {
            return (false, $"sc.exe Fehler: {ex.Message}");
        }
    }
}
