using System;
using System.Drawing;
using System.Windows.Forms;

namespace DwDocExport;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    // --- Eingabefelder ---
    private TextBox txtServer;
    private TextBox txtOrganization;
    private TextBox txtUser;
    private TextBox txtPassword;
    private ComboBox cboAuthMode;
    private TextBox txtOAuthClientId;
    private TextBox txtOAuthClientSecret;
    private ComboBox cboArchive;
    private TextBox txtOutputRoot;
    private TextBox txtStateDb;
    private TextBox txtLogFile;
    private ComboBox cboDateField;
    private NumericUpDown numHashDepth;
    private NumericUpDown numPageSize;
    private NumericUpDown numDelay;
    private NumericUpDown numRetries;
    private NumericUpDown numParallel;
    private CheckBox chkPerSection;
    private CheckBox chkMetadata;
    private CheckBox chkIncremental;
    private NumericUpDown numRescan;

    // --- Aktionsschaltflächen ---
    private Button btnLogin;
    private Button btnLoadFields;
    private Button btnTest;
    private Button btnSave;
    private Button btnOpenLog;
    private Button btnBrowseOutput;

    private Button btnSvcInstall;
    private Button btnSvcStart;
    private Button btnSvcStop;
    private Button btnSvcUninstall;

    private Label lblServiceStatus;
    private Label lblProgress;
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

        // ---------- Hilfsfunktionen ----------
        Label Lbl(Control parent, string text, int x, int y, int w = 160, Font? f = null, Color? color = null)
        {
            var l = new Label
            {
                Text = text, Left = x, Top = y + 3, Width = w, AutoSize = false,
                Font = f ?? Theme.Base(), ForeColor = color ?? Theme.Text,
                BackColor = Color.Transparent
            };
            parent.Controls.Add(l);
            return l;
        }

        void SectionTitle(Control parent, string text, int x, int y)
        {
            parent.Controls.Add(new Label
            {
                Text = text, Left = x, Top = y, AutoSize = true,
                Font = Theme.Section(), ForeColor = Theme.Accent, BackColor = Color.Transparent
            });
        }

        const int cardW = 712;
        const int lblX = 16;
        const int ctlX = 180;
        const int ctlW = 512;

        // ===================================================================
        //  Kopfzeile
        // ===================================================================
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Accent };
        header.Controls.Add(new Label
        {
            Text = "DwDocExport", Left = 18, Top = 9, AutoSize = true,
            Font = Theme.Title(), ForeColor = Color.White, BackColor = Color.Transparent
        });
        header.Controls.Add(new Label
        {
            Text = "DocuWare-Dokumente im Originalformat exportieren · Cloud & On-Premise",
            Left = 20, Top = 39, AutoSize = true,
            Font = Theme.Subtitle(), ForeColor = Color.FromArgb(214, 230, 246), BackColor = Color.Transparent
        });

        // ===================================================================
        //  Tabs
        // ===================================================================
        tabs = new TabControl { Dock = DockStyle.Fill, Font = Theme.Base(), Padding = new Point(14, 6) };

        var tabConn = new TabPage("  Verbindung & Anmeldung  ") { BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(14) };
        var tabExport = new TabPage("  Export  ") { BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(14) };
        var tabService = new TabPage("  Dienst & Protokoll  ") { BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(14) };
        tabs.TabPages.AddRange(new[] { tabConn, tabExport, tabService });

        // ------------------------------------------------------------------
        //  Tab 1: Verbindung & Anmeldung
        // ------------------------------------------------------------------
        // Karte A: Verbindungsdaten
        var cardA = Theme.Card(14, 14, cardW, 290);
        tabConn.Controls.Add(cardA);
        SectionTitle(cardA, "Verbindungsdaten", lblX, 12);
        int ay = 44;
        Lbl(cardA, "Server:", lblX, ay); txtServer = new TextBox { Left = ctlX, Top = ay, Width = ctlW }; cardA.Controls.Add(txtServer); ay += 32;
        Lbl(cardA, "Organisation:", lblX, ay); txtOrganization = new TextBox { Left = ctlX, Top = ay, Width = ctlW }; cardA.Controls.Add(txtOrganization); ay += 32;
        Lbl(cardA, "Benutzer:", lblX, ay); txtUser = new TextBox { Left = ctlX, Top = ay, Width = ctlW }; cardA.Controls.Add(txtUser); ay += 32;
        Lbl(cardA, "Passwort:", lblX, ay); txtPassword = new TextBox { Left = ctlX, Top = ay, Width = ctlW, UseSystemPasswordChar = true }; cardA.Controls.Add(txtPassword); ay += 32;
        Lbl(cardA, "Authentifizierung:", lblX, ay);
        cboAuthMode = new ComboBox { Left = ctlX, Top = ay, Width = 180, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        cboAuthMode.Items.AddRange(new object[] { "Auto", "Cookie", "Token" });
        cardA.Controls.Add(cboAuthMode); ay += 32;
        Lbl(cardA, "OAuth Client-ID:", lblX, ay); txtOAuthClientId = new TextBox { Left = ctlX, Top = ay, Width = ctlW }; cardA.Controls.Add(txtOAuthClientId);
        Lbl(cardA, "(optional)", ctlX + ctlW - 70, ay, 70, Theme.Subtitle(), Theme.Subtle); ay += 32;
        Lbl(cardA, "OAuth Client-Secret:", lblX, ay); txtOAuthClientSecret = new TextBox { Left = ctlX, Top = ay, Width = ctlW, UseSystemPasswordChar = true }; cardA.Controls.Add(txtOAuthClientSecret); ay += 32;

        // Karte B: Archiv & Aktionen
        var cardB = Theme.Card(14, 318, cardW, 188);
        tabConn.Controls.Add(cardB);
        SectionTitle(cardB, "Archiv & Felder", lblX, 12);
        btnLogin = new Button { Text = "Anmelden / Archive laden", Left = lblX, Top = 42, Width = 230 };
        btnLogin.Click += BtnLogin_Click; cardB.Controls.Add(btnLogin);
        btnTest = new Button { Text = "Verbindung testen", Left = lblX + 244, Top = 42, Width = 170 };
        btnTest.Click += BtnTest_Click; cardB.Controls.Add(btnTest);

        Lbl(cardB, "Archiv:", lblX, 86);
        cboArchive = new ComboBox { Left = ctlX, Top = 86, Width = ctlW, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        cboArchive.SelectedIndexChanged += CboArchive_SelectedIndexChanged; cardB.Controls.Add(cboArchive);

        Lbl(cardB, "Datumsfeld (Ordner):", lblX, 122);
        cboDateField = new ComboBox { Left = ctlX, Top = 122, Width = 372, DropDownStyle = ComboBoxStyle.DropDown };
        cardB.Controls.Add(cboDateField);
        btnLoadFields = new Button { Text = "Indexfelder laden", Left = ctlX + 384, Top = 121, Width = 128 };
        btnLoadFields.Click += BtnLoadFields_Click; cardB.Controls.Add(btnLoadFields);

        // ------------------------------------------------------------------
        //  Tab 2: Export
        // ------------------------------------------------------------------
        // Karte C: Speicherorte
        var cardC = Theme.Card(14, 14, cardW, 158);
        tabExport.Controls.Add(cardC);
        SectionTitle(cardC, "Speicherorte", lblX, 12);
        int cy = 44;
        Lbl(cardC, "Ausgabeordner:", lblX, cy);
        txtOutputRoot = new TextBox { Left = ctlX, Top = cy, Width = 372 }; cardC.Controls.Add(txtOutputRoot);
        btnBrowseOutput = new Button { Text = "Durchsuchen…", Left = ctlX + 384, Top = cy - 1, Width = 128 };
        btnBrowseOutput.Click += BtnBrowseOutput_Click; cardC.Controls.Add(btnBrowseOutput); cy += 36;
        Lbl(cardC, "Status-DB (SQLite):", lblX, cy);
        txtStateDb = new TextBox { Left = ctlX, Top = cy, Width = ctlW }; cardC.Controls.Add(txtStateDb); cy += 36;
        Lbl(cardC, "Logdatei:", lblX, cy);
        txtLogFile = new TextBox { Left = ctlX, Top = cy, Width = 372 }; cardC.Controls.Add(txtLogFile);
        btnOpenLog = new Button { Text = "Logdatei öffnen", Left = ctlX + 384, Top = cy - 1, Width = 128 };
        btnOpenLog.Click += BtnOpenLog_Click; cardC.Controls.Add(btnOpenLog);

        // Karte D: Optionen (zweispaltig für die Zahlenfelder)
        var cardD = Theme.Card(14, 186, cardW, 312);
        tabExport.Controls.Add(cardD);
        SectionTitle(cardD, "Optionen", lblX, 12);
        int col1L = lblX, col1C = 200, col2L = 372, col2C = 560;
        int numW = 110;
        int dy = 46;
        Lbl(cardD, "Seitengröße:", col1L, dy, 180);
        numPageSize = new NumericUpDown { Left = col1C, Top = dy, Width = numW, Minimum = 1, Maximum = 5000 }; cardD.Controls.Add(numPageSize);
        Lbl(cardD, "Parallele Downloads:", col2L, dy, 185);
        numParallel = new NumericUpDown { Left = col2C, Top = dy, Width = numW, Minimum = 1, Maximum = 32 }; cardD.Controls.Add(numParallel); dy += 34;

        Lbl(cardD, "Verzögerung (ms):", col1L, dy, 180);
        numDelay = new NumericUpDown { Left = col1C, Top = dy, Width = numW, Minimum = 0, Maximum = 60000 }; cardD.Controls.Add(numDelay);
        Lbl(cardD, "Max. Wiederholungen:", col2L, dy, 185);
        numRetries = new NumericUpDown { Left = col2C, Top = dy, Width = numW, Minimum = 0, Maximum = 20 }; cardD.Controls.Add(numRetries); dy += 34;

        Lbl(cardD, "Hash-Unterordner (0-2):", col1L, dy, 180);
        numHashDepth = new NumericUpDown { Left = col1C, Top = dy, Width = numW, Minimum = 0, Maximum = 2 }; cardD.Controls.Add(numHashDepth);
        Lbl(cardD, "Nachscannen (Min):", col2L, dy, 185);
        numRescan = new NumericUpDown { Left = col2C, Top = dy, Width = numW, Minimum = 0, Maximum = 100000 }; cardD.Controls.Add(numRescan); dy += 42;

        Lbl(cardD, "0 = einmaliger Export · > 0 = periodisch nachscannen", col1L, dy, cardW - 32, Theme.Subtitle(), Theme.Subtle); dy += 30;

        chkPerSection = new CheckBox { Text = "Pro Sektion herunterladen", Left = col1L, Top = dy, Width = 660, Font = Theme.Base() }; cardD.Controls.Add(chkPerSection); dy += 26;
        chkMetadata = new CheckBox { Text = "Metadaten-Sidecar (.metadata.json) je Dokument schreiben", Left = col1L, Top = dy, Width = 660, Font = Theme.Base() }; cardD.Controls.Add(chkMetadata); dy += 26;
        chkIncremental = new CheckBox { Text = "Inkrementell nachscannen (frühzeitig abbrechen)", Left = col1L, Top = dy, Width = 660, Font = Theme.Base() }; cardD.Controls.Add(chkIncremental);

        btnSave = new Button { Text = "Einstellungen speichern", Left = 14, Top = 508, Width = 200 };
        btnSave.Click += BtnSave_Click; tabExport.Controls.Add(btnSave);

        // ------------------------------------------------------------------
        //  Tab 3: Dienst & Protokoll
        // ------------------------------------------------------------------
        var cardE = Theme.Card(14, 14, cardW, 130);
        tabService.Controls.Add(cardE);
        SectionTitle(cardE, "Windows-Dienst", lblX, 12);
        btnSvcInstall = new Button { Text = "Installieren", Left = lblX, Top = 44, Width = 160 };
        btnSvcInstall.Click += BtnSvcInstall_Click; cardE.Controls.Add(btnSvcInstall);
        btnSvcStart = new Button { Text = "Starten", Left = lblX + 172, Top = 44, Width = 150 };
        btnSvcStart.Click += BtnSvcStart_Click; cardE.Controls.Add(btnSvcStart);
        btnSvcStop = new Button { Text = "Stoppen", Left = lblX + 334, Top = 44, Width = 150 };
        btnSvcStop.Click += BtnSvcStop_Click; cardE.Controls.Add(btnSvcStop);
        btnSvcUninstall = new Button { Text = "Deinstallieren", Left = lblX + 496, Top = 44, Width = 160 };
        btnSvcUninstall.Click += BtnSvcUninstall_Click; cardE.Controls.Add(btnSvcUninstall);

        lblServiceStatus = new Label { Text = "Dienststatus: –", Left = lblX, Top = 92, Width = 330, Font = Theme.Base() };
        cardE.Controls.Add(lblServiceStatus);
        lblProgress = new Label { Text = "Fortschritt: –", Left = lblX + 340, Top = 92, Width = 340, Font = Theme.Base() };
        cardE.Controls.Add(lblProgress);

        var cardF = Theme.Card(14, 156, cardW, 360);
        cardF.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        tabService.Controls.Add(cardF);
        SectionTitle(cardF, "Protokoll", lblX, 12);
        txtLog = new TextBox
        {
            Left = 12, Top = 40, Width = cardW - 26, Height = 308,
            Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true,
            BorderStyle = BorderStyle.None, BackColor = Color.White,
            Font = new Font("Consolas", 9F),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        cardF.Controls.Add(txtLog);

        // ===================================================================
        //  Fußzeile
        // ===================================================================
        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
        footer.Controls.Add(new Label
        {
            Text = "DwDocExport · .NET 8 · DocuWare Platform REST API",
            Left = 14, Top = 10, AutoSize = true, Font = Theme.Subtitle(), ForeColor = Theme.Subtle
        });
        lnkAuthor = new LinkLabel
        {
            Text = "Erstellt von Loheide.eu", AutoSize = true, Top = 10, Left = 600,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Font = Theme.Subtitle(), LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover
        };
        lnkAuthor.LinkClicked += LnkAuthor_LinkClicked;
        footer.Controls.Add(lnkAuthor);
        // Position rechts (wird bei Resize über Anchor gehalten).
        footer.Resize += (s, e) => lnkAuthor.Left = footer.Width - lnkAuthor.Width - 14;

        // ===================================================================
        //  Wurzel-Layout (Kopf / Tabs / Fuß) – deterministisch via TableLayout
        // ===================================================================
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        // Button-Stile zuweisen.
        Theme.Primary(btnLogin);
        Theme.Primary(btnSave);
        Theme.Primary(btnSvcStart);
        Theme.Secondary(btnTest);
        Theme.Secondary(btnLoadFields);
        Theme.Secondary(btnBrowseOutput);
        Theme.Secondary(btnOpenLog);
        Theme.Secondary(btnSvcInstall);
        Theme.Secondary(btnSvcStop);
        Theme.Secondary(btnSvcUninstall);

        // Status-Timer
        statusTimer = new System.Windows.Forms.Timer(components) { Interval = 2000 };
        statusTimer.Tick += StatusTimer_Tick;

        // ===================================================================
        //  Formular
        // ===================================================================
        AutoScaleMode = AutoScaleMode.Font;
        Font = Theme.Base();
        BackColor = Theme.Bg;
        ClientSize = new Size(760, 640);
        MinimumSize = new Size(776, 600);
        Text = "DwDocExport – DocuWare Dokument-Export";
        StartPosition = FormStartPosition.CenterScreen;

        ResumeLayout(false);
    }
}
