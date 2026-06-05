using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DwDocExport;

/// <summary>
/// Hauptfenster (GUI-Modus). Ablauf: erst anmelden und Archiv im Dropdown
/// wählen, dann Einstellungen speichern und Dienst installieren/starten.
/// Alle Aktionen sind gegen Ausnahmen abgesichert (keine Abstürze).
/// </summary>
public partial class MainForm : Form
{
    private ExporterOptions _opt;

    /// <summary>Eintrag der Archiv-ComboBox: Anzeige = Name, Wert = Id.</summary>
    private sealed record ArchiveItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    public MainForm()
    {
        InitializeComponent();
        _opt = ExporterOptions.Load();
        // Datei-Log auch für GUI-Meldungen aktivieren.
        FileLog.Configure(_opt.EffectiveLogPath);
        LoadOptionsIntoUi();
        statusTimer.Start();
    }

    // =====================================================================
    //  Konfiguration <-> Oberfläche
    // =====================================================================

    private void LoadOptionsIntoUi()
    {
        txtServer.Text = _opt.Server;
        txtOrganization.Text = _opt.Organization;
        txtUser.Text = _opt.User;
        txtPassword.Text = _opt.Password;
        cboAuthMode.SelectedItem = _opt.AuthMode.ToString();
        if (cboAuthMode.SelectedIndex < 0) cboAuthMode.SelectedIndex = 0;
        txtOAuthClientId.Text = _opt.OAuthClientId;
        txtOAuthClientSecret.Text = _opt.OAuthClientSecret;

        txtOutputRoot.Text = _opt.OutputRoot;
        txtStateDb.Text = _opt.StateDbPath;
        txtLogFile.Text = _opt.LogFilePath;
        cboDateField.Text = _opt.DateFieldName;
        numHashDepth.Value = Clamp(_opt.FolderHashDepth, numHashDepth.Minimum, numHashDepth.Maximum);
        numPageSize.Value = Clamp(_opt.PageSize, numPageSize.Minimum, numPageSize.Maximum);
        numDelay.Value = Clamp(_opt.DelayMs, numDelay.Minimum, numDelay.Maximum);
        numRetries.Value = Clamp(_opt.MaxRetries, numRetries.Minimum, numRetries.Maximum);
        numParallel.Value = Clamp(_opt.MaxParallelDownloads, numParallel.Minimum, numParallel.Maximum);
        numRescan.Value = Clamp(_opt.RescanIntervalMinutes, numRescan.Minimum, numRescan.Maximum);
        chkPerSection.Checked = _opt.DownloadPerSection;
        chkMetadata.Checked = _opt.WriteMetadataSidecar;
        chkIncremental.Checked = _opt.Incremental;

        if (!string.IsNullOrWhiteSpace(_opt.FileCabinetId))
        {
            cboArchive.Items.Clear();
            cboArchive.Items.Add(new ArchiveItem(_opt.FileCabinetId, $"(gespeichert) {_opt.FileCabinetId}"));
            cboArchive.SelectedIndex = 0;
        }
    }

    private void ReadUiIntoOptions()
    {
        _opt.Server = txtServer.Text.Trim();
        _opt.Organization = txtOrganization.Text.Trim();
        _opt.User = txtUser.Text;
        _opt.Password = txtPassword.Text;
        _opt.AuthMode = Enum.TryParse<AuthMode>(cboAuthMode.SelectedItem?.ToString(), out var am) ? am : AuthMode.Auto;
        _opt.OAuthClientId = txtOAuthClientId.Text.Trim();
        _opt.OAuthClientSecret = txtOAuthClientSecret.Text;

        _opt.OutputRoot = txtOutputRoot.Text.Trim();
        _opt.StateDbPath = txtStateDb.Text.Trim();
        _opt.LogFilePath = txtLogFile.Text.Trim();
        _opt.DateFieldName = cboDateField.Text.Trim();
        _opt.FolderHashDepth = (int)numHashDepth.Value;
        _opt.PageSize = (int)numPageSize.Value;
        _opt.DelayMs = (int)numDelay.Value;
        _opt.MaxRetries = (int)numRetries.Value;
        _opt.MaxParallelDownloads = (int)numParallel.Value;
        _opt.RescanIntervalMinutes = (int)numRescan.Value;
        _opt.DownloadPerSection = chkPerSection.Checked;
        _opt.WriteMetadataSidecar = chkMetadata.Checked;
        _opt.Incremental = chkIncremental.Checked;

        if (cboArchive.SelectedItem is ArchiveItem ci)
            _opt.FileCabinetId = ci.Id;

        // Logziel ggf. an neue Einstellung anpassen.
        FileLog.Configure(_opt.EffectiveLogPath);
    }

    private static decimal Clamp(int value, decimal min, decimal max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// <summary>Prüft die Konfiguration. Liefert eine Liste von Problemen (leer = ok).</summary>
    private List<string> Validate(bool requireArchive)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(_opt.Server) ||
            !Uri.TryCreate(_opt.Server, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            problems.Add("Server muss eine gültige http(s)-URL sein.");

        if (string.IsNullOrWhiteSpace(_opt.User))
            problems.Add("Benutzer darf nicht leer sein.");

        if (string.IsNullOrWhiteSpace(_opt.OutputRoot))
            problems.Add("Ausgabeordner darf nicht leer sein.");
        else
        {
            try
            {
                Directory.CreateDirectory(_opt.OutputRoot);
            }
            catch (Exception ex)
            {
                problems.Add($"Ausgabeordner nicht beschreibbar: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(_opt.StateDbPath))
            problems.Add("Status-DB-Pfad darf nicht leer sein.");

        if (requireArchive && string.IsNullOrWhiteSpace(_opt.FileCabinetId))
            problems.Add("Bitte zuerst ein Archiv wählen.");

        return problems;
    }

    private bool ValidateAndReport(bool requireArchive)
    {
        var problems = Validate(requireArchive);
        if (problems.Count == 0)
            return true;

        Log("Konfiguration unvollständig:");
        foreach (var p in problems)
            Log("  • " + p);
        return false;
    }

    // =====================================================================
    //  Schaltflächen
    // =====================================================================

    private async void BtnLogin_Click(object? sender, EventArgs e)
    {
        ReadUiIntoOptions();
        if (!ValidateAndReport(requireArchive: false))
            return;

        await RunGuardedAsync("Anmelden", async ct =>
        {
            using var client = new DocuWareClient(_opt, Log);
            await client.AuthenticateAsync(ct);
            Log("Anmeldung erfolgreich. Lade Archive …");

            var cabinets = await client.GetFileCabinetsAsync(ct);
            cboArchive.Items.Clear();
            foreach (var c in cabinets)
                cboArchive.Items.Add(new ArchiveItem(c.Id, c.Name));

            if (cboArchive.Items.Count > 0)
            {
                var idx = 0;
                for (var i = 0; i < cboArchive.Items.Count; i++)
                    if (cboArchive.Items[i] is ArchiveItem it && it.Id == _opt.FileCabinetId)
                        idx = i;
                cboArchive.SelectedIndex = idx;
            }
            Log($"{cabinets.Count} Archiv(e) geladen.");
        });
    }

    private async void BtnLoadFields_Click(object? sender, EventArgs e)
    {
        ReadUiIntoOptions();
        if (!ValidateAndReport(requireArchive: true))
            return;

        await RunGuardedAsync("Indexfelder laden", async ct =>
        {
            using var client = new DocuWareClient(_opt, Log);
            await client.AuthenticateAsync(ct);
            var fields = await client.GetFieldNamesAsync(_opt.FileCabinetId, ct);

            var current = cboDateField.Text;
            cboDateField.Items.Clear();
            cboDateField.Items.Add(""); // leer = keine Datumsordner
            foreach (var f in fields)
                cboDateField.Items.Add(f);
            cboDateField.Text = current;
            Log($"{fields.Count} Indexfelder geladen.");
        });
    }

    private async void BtnTest_Click(object? sender, EventArgs e)
    {
        ReadUiIntoOptions();
        if (!ValidateAndReport(requireArchive: false))
            return;

        await RunGuardedAsync("Verbindung testen", async ct =>
        {
            using var client = new DocuWareClient(_opt, Log);
            await client.AuthenticateAsync(ct);
            Log("Anmeldung erfolgreich.");

            if (!string.IsNullOrWhiteSpace(_opt.FileCabinetId))
            {
                var count = await client.GetDocumentCountAsync(_opt.FileCabinetId, ct);
                Log($"Archiv enthält {count} Dokument(e).");
            }
            else
            {
                Log("Kein Archiv gewählt – nur Anmeldung getestet.");
            }
        });
    }

    private void BtnSave_Click(object? sender, EventArgs e)
    {
        try
        {
            ReadUiIntoOptions();
            if (!ValidateAndReport(requireArchive: false))
                return;
            _opt.Save();
            Log($"Konfiguration gespeichert: {ExporterOptions.DefaultPath}");
        }
        catch (Exception ex)
        {
            Log($"Fehler beim Speichern: {ex.Message}");
        }
    }

    private void BtnBrowseOutput_Click(object? sender, EventArgs e)
    {
        try
        {
            using var dlg = new FolderBrowserDialog { Description = "Ausgabeordner wählen" };
            if (!string.IsNullOrWhiteSpace(txtOutputRoot.Text) && Directory.Exists(txtOutputRoot.Text))
                dlg.SelectedPath = txtOutputRoot.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK)
                txtOutputRoot.Text = dlg.SelectedPath;
        }
        catch (Exception ex)
        {
            Log($"Ordnerauswahl fehlgeschlagen: {ex.Message}");
        }
    }

    private void BtnOpenLog_Click(object? sender, EventArgs e)
    {
        try
        {
            ReadUiIntoOptions();
            var path = _opt.EffectiveLogPath;
            if (!File.Exists(path))
            {
                Log($"Noch keine Logdatei vorhanden: {path}");
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"Logdatei konnte nicht geöffnet werden: {ex.Message}");
        }
    }

    private void CboArchive_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (cboArchive.SelectedItem is ArchiveItem ci)
            _opt.FileCabinetId = ci.Id;
    }

    private void LnkAuthor_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://loheide.eu") { UseShellExecute = true });
        }
        catch
        {
            // Browser konnte nicht geöffnet werden – ignorieren.
        }
    }

    // =====================================================================
    //  Dienststeuerung
    // =====================================================================

    private void BtnSvcInstall_Click(object? sender, EventArgs e)
    {
        try
        {
            ReadUiIntoOptions();
            if (!ValidateAndReport(requireArchive: true))
                return;
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
            ReadUiIntoOptions();
            if (!ValidateAndReport(requireArchive: true))
                return;
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

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var status = ServiceManager.GetStatus();
            lblServiceStatus.Text = $"Dienststatus: {status}";
            lblServiceStatus.ForeColor =
                status == "Running" ? Theme.Success :
                status == "Nicht installiert" ? Theme.Subtle : Theme.Text;

            var dbPath = txtStateDb.Text.Trim();
            if (!string.IsNullOrWhiteSpace(dbPath) && File.Exists(dbPath))
            {
                try
                {
                    using var store = new ExportStateStore(dbPath);
                    var done = store.CountDone();
                    var err = store.CountError();
                    lblProgress.Text = $"Fortschritt: {done} erledigt, {err} Fehler";
                    lblProgress.ForeColor = err > 0 ? Theme.Danger : Theme.Text;
                }
                catch
                {
                    // DB evtl. gerade in Benutzung – ignorieren.
                }
            }
            else
            {
                lblProgress.Text = "Fortschritt: (keine DB)";
                lblProgress.ForeColor = Theme.Subtle;
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

    /// <summary>Schreibt eine Meldung mit Zeitstempel in die Log-TextBox und das Datei-Log.</summary>
    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(Log), message);
            return;
        }

        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        FileLog.Write("[GUI] " + message);
    }
}
