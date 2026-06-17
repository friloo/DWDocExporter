using System;
using System.Drawing;
using System.Windows.Forms;

namespace DwDocExport;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    private PropertyGrid propGrid;

    private ComboBox cboProfile;
    private Button btnProfileNew;
    private Button btnProfileDelete;
    private Button btnProfileSave;

    private Button btnLogin;
    private Button btnTest;
    private ComboBox cboArchive;
    private ComboBox cboDateField;
    private Button btnLoadFields;

    private Button btnSave;
    private Button btnReload;

    private Button btnExportNow;
    private Button btnDryRun;
    private Button btnVerify;
    private Button btnRetry;
    private Button btnCancel;
    private Button btnZip;
    private Button btnManifest;
    private ProgressBar progressBar;

    private Button btnSvcInstall;
    private Button btnSvcStart;
    private Button btnSvcStop;
    private Button btnSvcUninstall;
    private Label lblServiceStatus;
    private Label lblProgress;

    // Tab 3 – Schnelleinstellungen / Filter / Aktionen
    private TextBox txtQuickOutput;
    private Button btnBrowseOutput;
    private ComboBox cboQuickFormat;
    private TextBox txtQuickExt;
    private NumericUpDown numBatch;
    private ComboBox cboQuickFilterField;
    private TextBox txtQuickFrom;
    private TextBox txtQuickTo;
    private Button btnCountDocs;
    private Label lblDocCount;
    private Button btnOpenOutput;
    private Button btnOpenLog;
    private Button btnResetState;

    private LinkLabel lnkAuthor;
    private TextBox txtLog;
    private TabControl tabs;
    private System.Windows.Forms.Timer statusTimer;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null)
            components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        SuspendLayout();

        Label Lbl(Control parent, string text, int x, int y, int w = 160, Font? f = null, Color? color = null)
        {
            var l = new Label
            {
                Text = text, Left = x, Top = y + 3, Width = w, AutoSize = false,
                Font = f ?? Theme.Base(), ForeColor = color ?? Theme.Text, BackColor = Color.Transparent
            };
            parent.Controls.Add(l);
            return l;
        }
        void Section(Control parent, string text, int x, int y) => parent.Controls.Add(new Label
        {
            Text = text, Left = x, Top = y, AutoSize = true,
            Font = Theme.Section(), ForeColor = Theme.Accent, BackColor = Color.Transparent
        });

        const int cardW = 1140;

        // ---------- Header ----------
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Accent };
        header.Controls.Add(new Label { Text = "DwDocExport", Left = 18, Top = 9, AutoSize = true, Font = Theme.Title(), ForeColor = Color.White, BackColor = Color.Transparent });
        header.Controls.Add(new Label { Text = "DocuWare-Dokumente im Originalformat exportieren · Cloud & On-Premise", Left = 20, Top = 39, AutoSize = true, Font = Theme.Subtitle(), ForeColor = Color.FromArgb(214, 230, 246), BackColor = Color.Transparent });

        // ---------- Tabs ----------
        tabs = new TabControl { Dock = DockStyle.Fill, Font = Theme.Base(), Padding = new Point(14, 6) };
        var tabSettings = new TabPage("  Einstellungen  ") { BackColor = Theme.Bg, Padding = new Padding(12) };
        var tabConn = new TabPage("  Verbindung & Archiv  ") { BackColor = Theme.Bg, Padding = new Padding(12) };
        var tabRun = new TabPage("  Ausführen & Dienst  ") { BackColor = Theme.Bg, Padding = new Padding(12) };
        var tabLog = new TabPage("  Protokoll  ") { BackColor = Theme.Bg, Padding = new Padding(12) };
        tabs.TabPages.AddRange(new[] { tabSettings, tabConn, tabRun, tabLog });

        // ----- Tab Einstellungen -----
        Lbl(tabSettings, "Alle Einstellungen – nach Kategorie gruppiert. Geheimnisse werden verschlüsselt gespeichert.", 12, 6, 780, Theme.Subtitle(), Theme.Subtle);
        propGrid = new PropertyGrid
        {
            Left = 12, Top = 32, Width = cardW, Height = 470,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            PropertySort = PropertySort.Categorized,
            ToolbarVisible = true,
            HelpVisible = true
        };
        tabSettings.Controls.Add(propGrid);
        btnSave = new Button { Text = "Einstellungen speichern", Left = 12, Top = 510, Width = 200, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        btnSave.Click += BtnSave_Click; tabSettings.Controls.Add(btnSave);
        btnReload = new Button { Text = "Neu laden", Left = 222, Top = 510, Width = 120, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        btnReload.Click += BtnReload_Click; tabSettings.Controls.Add(btnReload);

        // ----- Tab Verbindung & Archiv -----
        var cardConn = Theme.CardPanel(12, 12, cardW, 210);
        tabConn.Controls.Add(cardConn);
        Section(cardConn, "Schritt 1 – Anmelden", 16, 12);
        btnLogin = new Button { Text = "Anmelden / Archive laden", Left = 16, Top = 44, Width = 240 };
        btnLogin.Click += BtnLogin_Click; cardConn.Controls.Add(btnLogin);
        btnTest = new Button { Text = "Verbindung testen", Left = 268, Top = 44, Width = 170 };
        btnTest.Click += BtnTest_Click; cardConn.Controls.Add(btnTest);
        Lbl(cardConn, "Die Verbindungsdaten werden im Tab \"Einstellungen\" gepflegt.", 16, 80, 740, Theme.Subtitle(), Theme.Subtle);

        Section(cardConn, "Schritt 2 – Archiv & Datumsfeld", 16, 108);
        Lbl(cardConn, "Archiv:", 16, 140, 90);
        cboArchive = new ComboBox { Left = 110, Top = 140, Width = 660, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        cboArchive.SelectedIndexChanged += CboArchive_SelectedIndexChanged; cardConn.Controls.Add(cboArchive);
        Lbl(cardConn, "Datumsfeld:", 16, 172, 90);
        cboDateField = new ComboBox { Left = 110, Top = 172, Width = 510, DropDownStyle = ComboBoxStyle.DropDown };
        cardConn.Controls.Add(cboDateField);
        btnLoadFields = new Button { Text = "Indexfelder laden", Left = 628, Top = 171, Width = 142 };
        btnLoadFields.Click += BtnLoadFields_Click; cardConn.Controls.Add(btnLoadFields);

        // ----- Tab Ausführen & Dienst -----
        var cardProfile = Theme.CardPanel(12, 12, cardW, 88);
        tabRun.Controls.Add(cardProfile);
        Section(cardProfile, "Profil (Job)", 16, 12);
        Lbl(cardProfile, "Profil:", 16, 46, 60);
        cboProfile = new ComboBox { Left = 80, Top = 46, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        cboProfile.SelectedIndexChanged += CboProfile_SelectedIndexChanged; cardProfile.Controls.Add(cboProfile);
        btnProfileNew = new Button { Text = "Neu", Left = 340, Top = 45, Width = 90 };
        btnProfileNew.Click += BtnProfileNew_Click; cardProfile.Controls.Add(btnProfileNew);
        btnProfileDelete = new Button { Text = "Löschen", Left = 436, Top = 45, Width = 100 };
        btnProfileDelete.Click += BtnProfileDelete_Click; cardProfile.Controls.Add(btnProfileDelete);
        btnProfileSave = new Button { Text = "Als Profil speichern", Left = 542, Top = 45, Width = 170 };
        btnProfileSave.Click += BtnProfileSave_Click; cardProfile.Controls.Add(btnProfileSave);

        var cardRun = Theme.CardPanel(12, 108, cardW, 170);
        tabRun.Controls.Add(cardRun);
        Section(cardRun, "Ausführen", 16, 12);
        btnExportNow = new Button { Text = "Export jetzt", Left = 16, Top = 44, Width = 150 };
        btnExportNow.Click += BtnExportNow_Click; cardRun.Controls.Add(btnExportNow);
        btnDryRun = new Button { Text = "Trockenlauf", Left = 172, Top = 44, Width = 140 };
        btnDryRun.Click += BtnDryRun_Click; cardRun.Controls.Add(btnDryRun);
        btnVerify = new Button { Text = "Verifizieren", Left = 318, Top = 44, Width = 140 };
        btnVerify.Click += BtnVerify_Click; cardRun.Controls.Add(btnVerify);
        btnRetry = new Button { Text = "Fehler erneut", Left = 464, Top = 44, Width = 150 };
        btnRetry.Click += BtnRetry_Click; cardRun.Controls.Add(btnRetry);
        btnCancel = new Button { Text = "Abbrechen", Left = 620, Top = 44, Width = 150 };
        btnCancel.Click += BtnCancel_Click; cardRun.Controls.Add(btnCancel);

        btnZip = new Button { Text = "Als ZIP packen", Left = 16, Top = 84, Width = 160 };
        btnZip.Click += BtnZip_Click; cardRun.Controls.Add(btnZip);
        btnManifest = new Button { Text = "Manifest schreiben", Left = 182, Top = 84, Width = 180 };
        btnManifest.Click += BtnManifest_Click; cardRun.Controls.Add(btnManifest);

        progressBar = new ProgressBar { Left = 16, Top = 124, Width = 754, Height = 18, Style = ProgressBarStyle.Continuous };
        cardRun.Controls.Add(progressBar);

        var cardSvc = Theme.CardPanel(12, 286, cardW, 120);
        tabRun.Controls.Add(cardSvc);
        Section(cardSvc, "Windows-Dienst", 16, 12);
        btnSvcInstall = new Button { Text = "Installieren", Left = 16, Top = 42, Width = 150 };
        btnSvcInstall.Click += BtnSvcInstall_Click; cardSvc.Controls.Add(btnSvcInstall);
        btnSvcStart = new Button { Text = "Starten", Left = 172, Top = 42, Width = 140 };
        btnSvcStart.Click += BtnSvcStart_Click; cardSvc.Controls.Add(btnSvcStart);
        btnSvcStop = new Button { Text = "Stoppen", Left = 318, Top = 42, Width = 140 };
        btnSvcStop.Click += BtnSvcStop_Click; cardSvc.Controls.Add(btnSvcStop);
        btnSvcUninstall = new Button { Text = "Deinstallieren", Left = 464, Top = 42, Width = 160 };
        btnSvcUninstall.Click += BtnSvcUninstall_Click; cardSvc.Controls.Add(btnSvcUninstall);
        lblServiceStatus = new Label { Text = "Dienststatus: –", Left = 16, Top = 86, Width = 360, Font = Theme.Base() };
        cardSvc.Controls.Add(lblServiceStatus);
        lblProgress = new Label { Text = "Fortschritt: –", Left = 384, Top = 86, Width = 390, Font = Theme.Base() };
        cardSvc.Controls.Add(lblProgress);

        // ----- Tab Ausführen: Schnelleinstellungen & Filter -----
        tabRun.AutoScroll = true;
        var cardQuick = Theme.CardPanel(12, 414, cardW, 196);
        tabRun.Controls.Add(cardQuick);
        Section(cardQuick, "Schnelleinstellungen & Filter", 16, 12);

        Lbl(cardQuick, "Ausgabeordner:", 16, 44, 120);
        txtQuickOutput = new TextBox { Left = 140, Top = 44, Width = cardW - 270, BackColor = Theme.InputBg, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        cardQuick.Controls.Add(txtQuickOutput);
        btnBrowseOutput = new Button { Text = "Durchsuchen …", Left = cardW - 120, Top = 43, Width = 104 };
        btnBrowseOutput.Click += BtnBrowseOutput_Click; cardQuick.Controls.Add(btnBrowseOutput);

        Lbl(cardQuick, "Zielformat:", 16, 80, 120);
        cboQuickFormat = new ComboBox { Left = 140, Top = 80, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        cboQuickFormat.Items.AddRange(new object[] { "Auto", "PDF", "PDFA" });
        cardQuick.Controls.Add(cboQuickFormat);
        Lbl(cardQuick, "Nur Endung (z. B. eml):", 280, 80, 150);
        txtQuickExt = new TextBox { Left = 432, Top = 80, Width = 120, BackColor = Theme.InputBg, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        cardQuick.Controls.Add(txtQuickExt);
        Lbl(cardQuick, "Max. pro Lauf (0=alle):", 580, 80, 160);
        numBatch = new NumericUpDown { Left = 744, Top = 80, Width = 100, Minimum = 0, Maximum = 1000000, BackColor = Theme.InputBg, ForeColor = Theme.Text };
        cardQuick.Controls.Add(numBatch);

        Lbl(cardQuick, "Datumsfilter – Feld:", 16, 120, 140);
        cboQuickFilterField = new ComboBox { Left = 160, Top = 120, Width = 220, DropDownStyle = ComboBoxStyle.DropDown };
        cardQuick.Controls.Add(cboQuickFilterField);
        Lbl(cardQuick, "von:", 400, 120, 40);
        txtQuickFrom = new TextBox { Left = 444, Top = 120, Width = 130, BackColor = Theme.InputBg, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        cardQuick.Controls.Add(txtQuickFrom);
        Lbl(cardQuick, "bis:", 588, 120, 40);
        txtQuickTo = new TextBox { Left = 628, Top = 120, Width = 130, BackColor = Theme.InputBg, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        cardQuick.Controls.Add(txtQuickTo);
        Lbl(cardQuick, "Datum als TT.MM.JJJJ oder ISO. Leer = kein Filter. Änderungen erscheinen auch im Tab \"Einstellungen\".", 16, 156, cardW - 40, Theme.Subtitle(), Theme.Subtle);

        txtQuickOutput.TextChanged += QuickChanged;
        cboQuickFormat.SelectedIndexChanged += QuickChanged;
        txtQuickExt.TextChanged += QuickChanged;
        numBatch.ValueChanged += QuickChanged;
        cboQuickFilterField.TextChanged += QuickChanged;
        cboQuickFilterField.SelectedIndexChanged += QuickChanged;
        txtQuickFrom.TextChanged += QuickChanged;
        txtQuickTo.TextChanged += QuickChanged;

        // ----- Tab Ausführen: Aktionen -----
        var cardActions = Theme.CardPanel(12, 622, cardW, 110);
        tabRun.Controls.Add(cardActions);
        Section(cardActions, "Aktionen", 16, 12);
        btnCountDocs = new Button { Text = "Dokumente zählen", Left = 16, Top = 44, Width = 180 };
        btnCountDocs.Click += BtnCountDocs_Click; cardActions.Controls.Add(btnCountDocs);
        btnOpenOutput = new Button { Text = "Ausgabeordner öffnen", Left = 206, Top = 44, Width = 190 };
        btnOpenOutput.Click += BtnOpenOutput_Click; cardActions.Controls.Add(btnOpenOutput);
        btnOpenLog = new Button { Text = "Logdatei öffnen", Left = 406, Top = 44, Width = 160 };
        btnOpenLog.Click += BtnOpenLog_Click; cardActions.Controls.Add(btnOpenLog);
        btnResetState = new Button { Text = "Status-DB zurücksetzen", Left = 576, Top = 44, Width = 200 };
        btnResetState.Click += BtnResetState_Click; cardActions.Controls.Add(btnResetState);
        lblDocCount = new Label { Text = "Archiv: – Dokumente", Left = 16, Top = 82, Width = cardW - 40, Font = Theme.Base(), ForeColor = Theme.Subtle };
        cardActions.Controls.Add(lblDocCount);

        // ----- Tab Protokoll -----
        var cardLog = Theme.CardPanel(12, 12, cardW, 500);
        cardLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        tabLog.Controls.Add(cardLog);
        Section(cardLog, "Protokoll", 16, 12);
        txtLog = new TextBox
        {
            Left = 12, Top = 40, Width = cardW - 26, Height = 448,
            Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true,
            BorderStyle = BorderStyle.None, BackColor = Theme.InputBg, ForeColor = Theme.Text,
            Font = new Font("Consolas", 9F),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        cardLog.Controls.Add(txtLog);

        // ---------- Footer ----------
        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
        footer.Controls.Add(new Label { Text = "DwDocExport · .NET 8 · DocuWare Platform REST API", Left = 14, Top = 10, AutoSize = true, Font = Theme.Subtitle(), ForeColor = Theme.Subtle });
        lnkAuthor = new LinkLabel { Text = "Erstellt von Loheide.eu", AutoSize = true, Top = 10, Left = 640, Anchor = AnchorStyles.Top | AnchorStyles.Right, Font = Theme.Subtitle(), LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover };
        lnkAuthor.LinkClicked += LnkAuthor_LinkClicked;
        footer.Controls.Add(lnkAuthor);
        footer.Resize += (s, e) => lnkAuthor.Left = footer.Width - lnkAuthor.Width - 14;

        // ---------- Wurzel-Layout ----------
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        // Button-Stile
        Theme.Primary(btnLogin); Theme.Primary(btnSave); Theme.Primary(btnExportNow); Theme.Primary(btnSvcStart);
        foreach (var b in new[] { btnTest, btnLoadFields, btnReload, btnProfileNew, btnProfileDelete, btnProfileSave,
                                  btnDryRun, btnVerify, btnRetry, btnCancel, btnZip, btnManifest,
                                  btnSvcInstall, btnSvcStop, btnSvcUninstall,
                                  btnBrowseOutput, btnCountDocs, btnOpenOutput, btnOpenLog, btnResetState })
            Theme.Secondary(b);

        statusTimer = new System.Windows.Forms.Timer(components) { Interval = 2000 };
        statusTimer.Tick += StatusTimer_Tick;

        // ---------- Formular ----------
        AutoScaleMode = AutoScaleMode.Font;
        Font = Theme.Base();
        BackColor = Theme.Bg;
        ClientSize = new Size(1180, 920);
        MinimumSize = new Size(900, 680);
        Text = "DwDocExport – DocuWare Dokument-Export";
        StartPosition = FormStartPosition.CenterScreen;

        ResumeLayout(false);
    }
}
