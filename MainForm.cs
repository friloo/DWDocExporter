using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DwDocExport;

/// <summary>
/// Hauptfenster (GUI). Bindet alle Einstellungen an ein PropertyGrid, verwaltet
/// Profile, steuert Anmeldung/Archivauswahl, den Windows-Dienst sowie direkte
/// Läufe (Export/Trockenlauf/Verifizieren/Fehler erneut) mit Fortschrittsanzeige.
/// </summary>
public partial class MainForm : Form
{
    private ExporterOptions _opt;
    private string _activeProfile = ProfileManager.DefaultProfileName;
    private bool _loadingProfiles;
    private bool _running;
    private CancellationTokenSource? _cts;

    private sealed record ArchiveItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    public MainForm()
    {
        _opt = ExporterOptions.Load();
        Theme.SetDark(_opt.DarkMode);   // muss vor dem UI-Aufbau gesetzt sein
        InitializeComponent();

        propGrid.SelectedObject = _opt;
        RefreshProfiles();
        FileLog.Configure(_opt.EffectiveLogPath, FileLog.ParseLevel(_opt.MinLogLevel), _opt.LogMaxSizeMb);

        Theme.Apply(tabs);
        Theme.ApplyToGrid(propGrid);
        ShowSavedArchive();
        SetRunningUi(false);
        statusTimer.Start();
    }

    // =====================================================================
    //  Profile
    // =====================================================================

    private void RefreshProfiles()
    {
        _loadingProfiles = true;
        cboProfile.Items.Clear();
        foreach (var p in ProfileManager.ListProfiles())
            cboProfile.Items.Add(p);
        cboProfile.SelectedItem = _activeProfile;
        if (cboProfile.SelectedIndex < 0 && cboProfile.Items.Count > 0)
            cboProfile.SelectedIndex = 0;
        _loadingProfiles = false;
    }

    private void CboProfile_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_loadingProfiles) return;
        _activeProfile = cboProfile.SelectedItem?.ToString() ?? ProfileManager.DefaultProfileName;
        _opt = ProfileManager.Load(_activeProfile);
        propGrid.SelectedObject = _opt;
        FileLog.Configure(_opt.EffectiveLogPath, FileLog.ParseLevel(_opt.MinLogLevel), _opt.LogMaxSizeMb);
        cboArchive.Items.Clear();
        ShowSavedArchive();
        Log($"Profil geladen: {_activeProfile}");
    }

    private void BtnProfileNew_Click(object? sender, EventArgs e)
    {
        var name = Prompt.Text(this, "Neues Profil", "Name des neuen Profils:");
        if (string.IsNullOrWhiteSpace(name)) return;
        ProfileManager.Save(new ExporterOptions(), name);
        RefreshProfiles();
        cboProfile.SelectedItem = name;
        Log($"Profil angelegt: {name}");
    }

    private void BtnProfileDelete_Click(object? sender, EventArgs e)
    {
        if (_activeProfile.Equals(ProfileManager.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
        {
            Log("Das Standardprofil kann nicht gelöscht werden.");
            return;
        }
        if (MessageBox.Show($"Profil „{_activeProfile}" löschen?", "Profil löschen",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        ProfileManager.Delete(_activeProfile);
        _activeProfile = ProfileManager.DefaultProfileName;
        RefreshProfiles();
        Log("Profil gelöscht.");
    }

    private void BtnProfileSave_Click(object? sender, EventArgs e)
    {
        var name = Prompt.Text(this, "Als Profil speichern", "Profilname:", _activeProfile);
        if (string.IsNullOrWhiteSpace(name)) return;
        ProfileManager.Save(_opt, name);
        RefreshProfiles();
        cboProfile.SelectedItem = name;
        Log($"Als Profil gespeichert: {name}");
    }

    // =====================================================================
    //  Speichern / Laden
    // =====================================================================

    private void BtnSave_Click(object? sender, EventArgs e)
    {
        try
        {
            if (!ValidateAndReport(false)) return;
            _opt.Save(ProfileManager.PathFor(_activeProfile));
            FileLog.Configure(_opt.EffectiveLogPath, FileLog.ParseLevel(_opt.MinLogLevel), _opt.LogMaxSizeMb);
            Log($"Einstellungen gespeichert ({_activeProfile}).");
        }
        catch (Exception ex) { Log($"Fehler beim Speichern: {ex.Message}"); }
    }

    private void BtnReload_Click(object? sender, EventArgs e)
    {
        _opt = ProfileManager.Load(_activeProfile);
        propGrid.SelectedObject = _opt;
        cboArchive.Items.Clear();
        ShowSavedArchive();
        Log("Einstellungen neu geladen.");
    }

    private void ShowSavedArchive()
    {
        if (!string.IsNullOrWhiteSpace(_opt.FileCabinetId))
        {
            cboArchive.Items.Add(new ArchiveItem(_opt.FileCabinetId, $"(gespeichert) {_opt.FileCabinetId}"));
            cboArchive.SelectedIndex = 0;
        }
    }

    // =====================================================================
    //  Validierung
    // =====================================================================

    private List<string> Validate(bool requireArchive)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(_opt.Server) ||
            !Uri.TryCreate(_opt.Server, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            p.Add("Server muss eine gültige http(s)-URL sein.");
        if (string.IsNullOrWhiteSpace(_opt.User)) p.Add("Benutzer darf nicht leer sein.");
        if (string.IsNullOrWhiteSpace(_opt.OutputRoot)) p.Add("Ausgabeordner darf nicht leer sein.");
        else
        {
            try { Directory.CreateDirectory(_opt.OutputRoot); }
            catch (Exception ex) { p.Add($"Ausgabeordner nicht beschreibbar: {ex.Message}"); }
        }
        if (string.IsNullOrWhiteSpace(_opt.StateDbPath)) p.Add("Status-DB-Pfad darf nicht leer sein.");
        if (requireArchive && string.IsNullOrWhiteSpace(_opt.FileCabinetId)) p.Add("Bitte zuerst ein Archiv wählen.");
        return p;
    }

    private bool ValidateAndReport(bool requireArchive)
    {
        var problems = Validate(requireArchive);
        if (problems.Count == 0) return true;
        Log("Konfiguration unvollständig:");
        foreach (var x in problems) Log("  • " + x);
        return false;
    }

    // =====================================================================
    //  Anmeldung / Archiv / Felder
    // =====================================================================

    private async void BtnLogin_Click(object? sender, EventArgs e)
    {
        if (!ValidateAndReport(false)) return;
        await RunGuardedAsync("Anmelden", async ct =>
        {
            using var client = new DocuWareClient(_opt, Log);
            await client.AuthenticateAsync(ct);
            Log("Anmeldung erfolgreich. Lade Archive …");
            var cabinets = await client.GetFileCabinetsAsync(ct);
            cboArchive.Items.Clear();
            foreach (var c in cabinets) cboArchive.Items.Add(new ArchiveItem(c.Id, c.Name));
            if (cboArchive.Items.Count > 0)
            {
                var idx = 0;
                for (var i = 0; i < cboArchive.Items.Count; i++)
                    if (cboArchive.Items[i] is ArchiveItem it && it.Id == _opt.FileCabinetId) idx = i;
                cboArchive.SelectedIndex = idx;
            }
            Log($"{cabinets.Count} Archiv(e) geladen.");
        });
    }

    private async void BtnLoadFields_Click(object? sender, EventArgs e)
    {
        if (!ValidateAndReport(true)) return;
        await RunGuardedAsync("Indexfelder laden", async ct =>
        {
            using var client = new DocuWareClient(_opt, Log);
            await client.AuthenticateAsync(ct);
            var fields = await client.GetFieldNamesAsync(_opt.FileCabinetId, ct);
            var current = cboDateField.Text;
            cboDateField.Items.Clear();
            cboDateField.Items.Add("");
            foreach (var f in fields) cboDateField.Items.Add(f);
            cboDateField.Text = current;
            Log($"{fields.Count} Indexfelder geladen.");
        });
    }

    private async void BtnTest_Click(object? sender, EventArgs e)
    {
        if (!ValidateAndReport(false)) return;
        await RunGuardedAsync("Verbindung testen", async ct =>
        {
            using var client = new DocuWareClient(_opt, Log);
            await client.AuthenticateAsync(ct);
            Log("Anmeldung erfolgreich.");
            if (!string.IsNullOrWhiteSpace(_opt.FileCabinetId))
                Log($"Archiv enthält {await client.GetDocumentCountAsync(_opt.FileCabinetId, ct)} Dokument(e).");
            else
                Log("Kein Archiv gewählt – nur Anmeldung getestet.");
        });
    }

    private void CboArchive_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (cboArchive.SelectedItem is ArchiveItem ci)
        {
            _opt.FileCabinetId = ci.Id;
            // Datumsfeld in die Optionen übernehmen, falls gewählt.
            if (!string.IsNullOrWhiteSpace(cboDateField.Text))
                _opt.DateFieldName = cboDateField.Text.Trim();
            propGrid.Refresh();
        }
    }

    // =====================================================================
    //  Läufe (Engine)
    // =====================================================================

    private async void BtnExportNow_Click(object? sender, EventArgs e) => await RunEngineAsync(ExportMode.Export);
    private async void BtnDryRun_Click(object? sender, EventArgs e) => await RunEngineAsync(ExportMode.DryRun);
    private async void BtnVerify_Click(object? sender, EventArgs e) => await RunEngineAsync(ExportMode.Verify);
    private async void BtnRetry_Click(object? sender, EventArgs e) => await RunEngineAsync(ExportMode.RetryErrors);
    private void BtnCancel_Click(object? sender, EventArgs e) => _cts?.Cancel();

    private async Task RunEngineAsync(ExportMode mode)
    {
        if (_running) { Log("Es läuft bereits eine Operation."); return; }

        // Datumsfeld aus dem Dropdown übernehmen.
        if (!string.IsNullOrWhiteSpace(cboDateField.Text))
            _opt.DateFieldName = cboDateField.Text.Trim();

        if (mode == ExportMode.Verify)
        {
            if (string.IsNullOrWhiteSpace(_opt.StateDbPath) || !File.Exists(_opt.StateDbPath))
            { Log("Keine Status-DB vorhanden – nichts zu verifizieren."); return; }
        }
        else if (!ValidateAndReport(true)) return;

        _running = true;
        SetRunningUi(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<ExportProgress>(OnProgress);

        try
        {
            var engine = new ExportEngine(_opt.Clone(), Log);
            var result = await Task.Run(() => engine.RunAsync(mode, progress, _cts.Token));
            Log(result.Summary);
        }
        catch (Exception ex) { Log($"Fehler: {ex.Message}"); }
        finally
        {
            _running = false;
            SetRunningUi(false);
            _cts?.Dispose();
            _cts = null;
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 0;
        }
    }

    private void OnProgress(ExportProgress p)
    {
        if (p.Total.HasValue && p.Total.Value > 0)
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Maximum = p.Total.Value;
            progressBar.Value = Math.Min(p.Done, p.Total.Value);
        }
        else
        {
            progressBar.Style = ProgressBarStyle.Marquee;
        }
        var totalTxt = p.Total.HasValue ? $" / {p.Total}" : "";
        lblProgress.Text = $"{p.Phase}: {p.Done}{totalTxt} ({p.DocsPerSec:0.0}/s)";
    }

    private void SetRunningUi(bool running)
    {
        btnExportNow.Enabled = !running;
        btnDryRun.Enabled = !running;
        btnVerify.Enabled = !running;
        btnRetry.Enabled = !running;
        btnZip.Enabled = !running;
        btnManifest.Enabled = !running;
        btnCancel.Enabled = running;
    }

    // =====================================================================
    //  ZIP / Manifest
    // =====================================================================

    private async void BtnZip_Click(object? sender, EventArgs e)
    {
        if (!Directory.Exists(_opt.OutputRoot)) { Log("Ausgabeordner existiert nicht."); return; }
        var choice = MessageBox.Show(
            "Gesamten Export in EINE ZIP-Datei packen?\n(Nein = je Jahr/Monat ein Archiv)",
            "ZIP packen", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;

        try
        {
            if (choice == DialogResult.Yes)
            {
                using var sfd = new SaveFileDialog { Filter = "ZIP-Archiv|*.zip", FileName = "export.zip" };
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                var path = sfd.FileName;
                Log("Packe …");
                Log(await Task.Run(() => Packaging.ZipWholeOutput(_opt.OutputRoot, path)));
            }
            else
            {
                using var fbd = new FolderBrowserDialog { Description = "Zielordner für Monats-Archive" };
                if (fbd.ShowDialog(this) != DialogResult.OK) return;
                var dir = fbd.SelectedPath;
                Log("Packe je Jahr/Monat …");
                Log(await Task.Run(() => Packaging.ZipPerYearMonth(_opt.OutputRoot, dir)));
            }
        }
        catch (Exception ex) { Log($"ZIP-Fehler: {ex.Message}"); }
    }

    private void BtnManifest_Click(object? sender, EventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_opt.StateDbPath) || !File.Exists(_opt.StateDbPath))
            { Log("Keine Status-DB vorhanden."); return; }
            var path = Path.Combine(_opt.OutputRoot, "manifest.csv");
            using var store = new ExportStateStore(_opt.StateDbPath);
            store.ExportManifestCsv(path);
            Log($"Manifest geschrieben: {path}");
        }
        catch (Exception ex) { Log($"Manifest-Fehler: {ex.Message}"); }
    }

    // =====================================================================
    //  Dienststeuerung
    // =====================================================================

    private void BtnSvcInstall_Click(object? sender, EventArgs e)
    {
        try
        {
            if (!ValidateAndReport(true)) return;
            _opt.Save(ProfileManager.PathFor(_activeProfile));
            Log("Konfiguration gespeichert.");
            var (ok, output) = ServiceManager.Install(Application.ExecutablePath, _opt.ServiceAccount, _opt.ServicePassword);
            Log(output);
            Log(ok ? "Dienst installiert." : "Dienstinstallation fehlgeschlagen.");
        }
        catch (Exception ex) { Log($"Fehler bei Dienstinstallation: {ex.Message}"); }
    }

    private void BtnSvcStart_Click(object? sender, EventArgs e)
    {
        try
        {
            if (!ValidateAndReport(true)) return;
            _opt.Save(ProfileManager.PathFor(_activeProfile));
            Log(ServiceManager.Start().output);
        }
        catch (Exception ex) { Log($"Fehler beim Starten: {ex.Message}"); }
    }

    private void BtnSvcStop_Click(object? sender, EventArgs e)
    {
        try { Log(ServiceManager.Stop().output); }
        catch (Exception ex) { Log($"Fehler beim Stoppen: {ex.Message}"); }
    }

    private void BtnSvcUninstall_Click(object? sender, EventArgs e)
    {
        try
        {
            var (ok, output) = ServiceManager.Uninstall();
            Log(output);
            Log(ok ? "Dienst deinstalliert." : "Deinstallation fehlgeschlagen.");
        }
        catch (Exception ex) { Log($"Fehler bei Deinstallation: {ex.Message}"); }
    }

    private void LnkAuthor_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://loheide.eu") { UseShellExecute = true }); }
        catch { }
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

            if (_running) return; // während eines Laufs zeigt OnProgress den Fortschritt

            var dbPath = _opt.StateDbPath;
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
                catch { }
            }
            else
            {
                lblProgress.Text = "Fortschritt: (keine DB)";
                lblProgress.ForeColor = Theme.Subtle;
            }
        }
        catch { }
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
        catch (OperationCanceledException) { Log($"{title}: Zeitüberschreitung/Abbruch."); }
        catch (Exception ex) { Log($"{title}: Fehler – {ex.Message}"); }
        finally { Enabled = true; UseWaitCursor = false; }
    }

    private void Log(string message)
    {
        if (InvokeRequired) { BeginInvoke(new Action<string>(Log), message); return; }
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        FileLog.Write("[GUI] " + message);
    }
}
