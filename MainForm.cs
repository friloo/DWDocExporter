using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DwDocExport;

/// <summary>
/// Hauptfenster (GUI-Modus). Ablauf: erst anmelden und Schrank im Dropdown
/// wählen, dann Einstellungen speichern und Dienst installieren/starten.
/// Alle Aktionen sind gegen Ausnahmen abgesichert (keine Abstürze).
/// </summary>
public partial class MainForm : Form
{
    private ExporterOptions _opt;

    /// <summary>Eintrag der Schrank-ComboBox: Anzeige = Name, Wert = Id.</summary>
    private sealed record CabinetItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    public MainForm()
    {
        InitializeComponent();
        _opt = ExporterOptions.Load();
        LoadOptionsIntoUi();
        statusTimer.Start();
    }

    // =====================================================================
    //  Konfiguration <-> Oberfläche
    // =====================================================================

    /// <summary>Überträgt die geladene Konfiguration in die Steuerelemente.</summary>
    private void LoadOptionsIntoUi()
    {
        txtServer.Text = _opt.Server;
        txtOrganization.Text = _opt.Organization;
        txtUser.Text = _opt.User;
        txtPassword.Text = _opt.Password;
        cboAuthMode.SelectedItem = _opt.AuthMode.ToString();
        if (cboAuthMode.SelectedIndex < 0) cboAuthMode.SelectedIndex = 0;

        txtOutputRoot.Text = _opt.OutputRoot;
        txtStateDb.Text = _opt.StateDbPath;
        cboDateField.Text = _opt.DateFieldName;
        numHashDepth.Value = Clamp(_opt.FolderHashDepth, numHashDepth.Minimum, numHashDepth.Maximum);
        numPageSize.Value = Clamp(_opt.PageSize, numPageSize.Minimum, numPageSize.Maximum);
        numDelay.Value = Clamp(_opt.DelayMs, numDelay.Minimum, numDelay.Maximum);
        numRetries.Value = Clamp(_opt.MaxRetries, numRetries.Minimum, numRetries.Maximum);
        numRescan.Value = Clamp(_opt.RescanIntervalMinutes, numRescan.Minimum, numRescan.Maximum);
        chkPerSection.Checked = _opt.DownloadPerSection;

        // Falls bereits eine FileCabinetId konfiguriert ist, als einzelnen Eintrag anzeigen.
        if (!string.IsNullOrWhiteSpace(_opt.FileCabinetId))
        {
            cboFileCabinet.Items.Clear();
            cboFileCabinet.Items.Add(new CabinetItem(_opt.FileCabinetId, $"(gespeichert) {_opt.FileCabinetId}"));
            cboFileCabinet.SelectedIndex = 0;
        }
    }

    /// <summary>Liest die aktuellen Steuerelemente in das Optionsobjekt zurück.</summary>
    private void ReadUiIntoOptions()
    {
        _opt.Server = txtServer.Text.Trim();
        _opt.Organization = txtOrganization.Text.Trim();
        _opt.User = txtUser.Text;
        _opt.Password = txtPassword.Text;
        _opt.AuthMode = Enum.TryParse<AuthMode>(cboAuthMode.SelectedItem?.ToString(), out var am) ? am : AuthMode.Auto;

        _opt.OutputRoot = txtOutputRoot.Text.Trim();
        _opt.StateDbPath = txtStateDb.Text.Trim();
        _opt.DateFieldName = cboDateField.Text.Trim();
        _opt.FolderHashDepth = (int)numHashDepth.Value;
        _opt.PageSize = (int)numPageSize.Value;
        _opt.DelayMs = (int)numDelay.Value;
        _opt.MaxRetries = (int)numRetries.Value;
        _opt.RescanIntervalMinutes = (int)numRescan.Value;
        _opt.DownloadPerSection = chkPerSection.Checked;

        if (cboFileCabinet.SelectedItem is CabinetItem ci)
            _opt.FileCabinetId = ci.Id;
    }

    private static decimal Clamp(int value, decimal min, decimal max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    // =====================================================================
    //  Schaltflächen
    // =====================================================================

    /// <summary>Anmelden und alle Aktenschränke (ohne Baskets) ins Dropdown laden.</summary>
    private async void BtnLogin_Click(object? sender, EventArgs e)
    {
        ReadUiIntoOptions();
        await RunGuardedAsync("Anmelden", async ct =>
        {
            using var client = new DocuWareClient(_opt, Log);
            await client.AuthenticateAsync(ct);
            Log("Anmeldung erfolgreich. Lade Aktenschränke …");

            var cabinets = await client.GetFileCabinetsAsync(ct);
            cboFileCabinet.Items.Clear();
            foreach (var c in cabinets)
                cboFileCabinet.Items.Add(new CabinetItem(c.Id, c.Name));

            if (cboFileCabinet.Items.Count > 0)
            {
                // Bereits konfigurierten Schrank vorauswählen, sonst ersten.
                var idx = 0;
                for (var i = 0; i < cboFileCabinet.Items.Count; i++)
                    if (cboFileCabinet.Items[i] is CabinetItem it && it.Id == _opt.FileCabinetId)
                        idx = i;
                cboFileCabinet.SelectedIndex = idx;
            }
            Log($"{cabinets.Count} Aktenschrank/Schränke geladen.");
        });
    }

    /// <summary>Indexfelder des gewählten Schranks als Dropdown für das Datumsfeld laden.</summary>
    private async void BtnLoadFields_Click(object? sender, EventArgs e)
    {
        ReadUiIntoOptions();
        if (string.IsNullOrWhiteSpace(_opt.FileCabinetId))
        {
            Log("Bitte zuerst einen Aktenschrank wählen.");
            return;
        }

        await RunGuardedAsync("Indexfelder laden", async ct =>
        {
            using var client = new DocuWareClient(_opt, Log);
            await client.AuthenticateAsync(ct);
            var fields = await client.GetFieldNamesAsync(_opt.FileCabinetId, ct);

            var current = cboDateField.Text;
            cboDateField.Items.Clear();
            cboDateField.Items.Add(""); // leere Auswahl = keine Datumsordner
            foreach (var f in fields)
                cboDateField.Items.Add(f);
            cboDateField.Text = current;
            Log($"{fields.Count} Indexfelder geladen.");
        });
    }

    /// <summary>Verbindung testen: anmelden und Dokumentanzahl anzeigen.</summary>
    private async void BtnTest_Click(object? sender, EventArgs e)
    {
        ReadUiIntoOptions();
        await RunGuardedAsync("Verbindung testen", async ct =>
        {
            using var client = new DocuWareClient(_opt, Log);
            await client.AuthenticateAsync(ct);
            Log("Anmeldung erfolgreich.");

            if (!string.IsNullOrWhiteSpace(_opt.FileCabinetId))
            {
                var count = await client.GetDocumentCountAsync(_opt.FileCabinetId, ct);
                Log($"Schrank enthält {count} Dokument(e).");
            }
            else
            {
                Log("Kein Schrank gewählt – nur Anmeldung getestet.");
            }
        });
    }

    /// <summary>Konfiguration speichern.</summary>
    private void BtnSave_Click(object? sender, EventArgs e)
    {
        try
        {
            ReadUiIntoOptions();
            _opt.Save();
            Log($"Konfiguration gespeichert: {ExporterOptions.DefaultPath}");
        }
        catch (Exception ex)
        {
            Log($"Fehler beim Speichern: {ex.Message}");
        }
    }

    private void CboFileCabinet_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (cboFileCabinet.SelectedItem is CabinetItem ci)
            _opt.FileCabinetId = ci.Id;
    }

    // =====================================================================
    //  Dienststeuerung
    // =====================================================================

    private void BtnSvcInstall_Click(object? sender, EventArgs e)
    {
        try
        {
            // Vor dem Installieren stets speichern, damit der Dienst die aktuelle Konfiguration nutzt.
            ReadUiIntoOptions();
            _opt.Save();
            Log("Konfiguration gespeichert.");

            var exePath = Application.ExecutablePath;
            var (ok, output) = ServiceManager.Install(exePath);
            Log(output);
            Log(ok ? "Dienst installiert." : "Dienstinstallation fehlgeschlagen.");
        }
        catch (Exception ex)
        {
            Log($"Fehler bei Dienstinstallation: {ex.Message}");
        }
    }

    private void BtnSvcStart_Click(object? sender, EventArgs e)
    {
        try
        {
            // Vor dem Start speichern (aktuelle Einstellungen).
            ReadUiIntoOptions();
            _opt.Save();

            var (ok, output) = ServiceManager.Start();
            Log(output);
        }
        catch (Exception ex)
        {
            Log($"Fehler beim Starten: {ex.Message}");
        }
    }

    private void BtnSvcStop_Click(object? sender, EventArgs e)
    {
        try
        {
            var (ok, output) = ServiceManager.Stop();
            Log(output);
        }
        catch (Exception ex)
        {
            Log($"Fehler beim Stoppen: {ex.Message}");
        }
    }

    private void BtnSvcUninstall_Click(object? sender, EventArgs e)
    {
        try
        {
            var (ok, output) = ServiceManager.Uninstall();
            Log(output);
            Log(ok ? "Dienst deinstalliert." : "Deinstallation fehlgeschlagen.");
        }
        catch (Exception ex)
        {
            Log($"Fehler bei Deinstallation: {ex.Message}");
        }
    }

    // =====================================================================
    //  Status-Timer
    // =====================================================================

    /// <summary>Pollt Dienststatus und liest Fortschritt/Fehler aus der SQLite-DB.</summary>
    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            lblServiceStatus.Text = $"Dienststatus: {ServiceManager.GetStatus()}";

            var dbPath = txtStateDb.Text.Trim();
            if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
            {
                try
                {
                    using var store = new ExportStateStore(dbPath);
                    var done = store.CountDone();
                    var err = store.CountError();
                    lblProgress.Text = $"Fortschritt: {done} erledigt, {err} Fehler";
                }
                catch
                {
                    // DB evtl. gerade in Benutzung – stillschweigend ignorieren.
                }
            }
            else
            {
                lblProgress.Text = "Fortschritt: (keine DB)";
            }
        }
        catch
        {
            // Timer darf niemals abstürzen.
        }
    }

    // =====================================================================
    //  Hilfsfunktionen
    // =====================================================================

    /// <summary>
    /// Führt eine asynchrone Aktion gegen DocuWare aus, fängt alle Fehler ab
    /// und sperrt das Fenster währenddessen (kein Doppelklick).
    /// </summary>
    private async Task RunGuardedAsync(string title, Func<CancellationToken, Task> action)
    {
        Enabled = false;
        UseWaitCursor = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await action(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log($"{title}: Zeitüberschreitung/Abbruch.");
        }
        catch (Exception ex)
        {
            Log($"{title}: Fehler – {ex.Message}");
        }
        finally
        {
            Enabled = true;
            UseWaitCursor = false;
        }
    }

    /// <summary>Schreibt eine Meldung mit Zeitstempel in die Log-TextBox.</summary>
    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(Log), message);
            return;
        }

        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
