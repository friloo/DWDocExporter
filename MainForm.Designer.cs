using System;
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
    private ComboBox cboFileCabinet;
    private TextBox txtOutputRoot;
    private TextBox txtStateDb;
    private ComboBox cboDateField;
    private NumericUpDown numHashDepth;
    private NumericUpDown numPageSize;
    private NumericUpDown numDelay;
    private NumericUpDown numRetries;
    private CheckBox chkPerSection;
    private NumericUpDown numRescan;

    // --- Aktionsschaltflächen ---
    private Button btnLogin;
    private Button btnLoadFields;
    private Button btnTest;
    private Button btnSave;

    private Button btnSvcInstall;
    private Button btnSvcStart;
    private Button btnSvcStop;
    private Button btnSvcUninstall;

    private Label lblServiceStatus;
    private Label lblProgress;
    private TextBox txtLog;

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

        // Hilfsfunktionen für ein konsistentes Layout.
        int labelW = 150;
        int ctrlX = 170;
        int ctrlW = 360;
        int rowH = 30;
        int y = 15;

        Label MakeLabel(string text, int yy)
        {
            var l = new Label
            {
                Text = text,
                Left = 12,
                Top = yy + 3,
                Width = labelW,
                AutoSize = false
            };
            Controls.Add(l);
            return l;
        }

        // Server
        MakeLabel("Server:", y);
        txtServer = new TextBox { Left = ctrlX, Top = y, Width = ctrlW };
        Controls.Add(txtServer); y += rowH;

        // Organisation
        MakeLabel("Organisation:", y);
        txtOrganization = new TextBox { Left = ctrlX, Top = y, Width = ctrlW };
        Controls.Add(txtOrganization); y += rowH;

        // Benutzer
        MakeLabel("Benutzer:", y);
        txtUser = new TextBox { Left = ctrlX, Top = y, Width = ctrlW };
        Controls.Add(txtUser); y += rowH;

        // Passwort
        MakeLabel("Passwort:", y);
        txtPassword = new TextBox { Left = ctrlX, Top = y, Width = ctrlW, UseSystemPasswordChar = true };
        Controls.Add(txtPassword); y += rowH;

        // AuthMode
        MakeLabel("Authentifizierung:", y);
        cboAuthMode = new ComboBox
        {
            Left = ctrlX, Top = y, Width = 160,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cboAuthMode.Items.AddRange(new object[] { "Auto", "Cookie", "Token" });
        Controls.Add(cboAuthMode); y += rowH;

        // Anmelden-Button (lädt Schränke)
        btnLogin = new Button { Text = "Anmelden / Schränke laden", Left = ctrlX, Top = y, Width = 200 };
        btnLogin.Click += BtnLogin_Click;
        Controls.Add(btnLogin);
        btnTest = new Button { Text = "Verbindung testen", Left = ctrlX + 210, Top = y, Width = 150 };
        btnTest.Click += BtnTest_Click;
        Controls.Add(btnTest);
        y += rowH + 5;

        // Aktenschrank-Auswahl
        MakeLabel("Aktenschrank:", y);
        cboFileCabinet = new ComboBox
        {
            Left = ctrlX, Top = y, Width = ctrlW,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cboFileCabinet.SelectedIndexChanged += CboFileCabinet_SelectedIndexChanged;
        Controls.Add(cboFileCabinet); y += rowH;

        // Indexfeld für Datumsordner
        MakeLabel("Datumsfeld (Ordner):", y);
        cboDateField = new ComboBox { Left = ctrlX, Top = y, Width = 250, DropDownStyle = ComboBoxStyle.DropDown };
        Controls.Add(cboDateField);
        btnLoadFields = new Button { Text = "Indexfelder laden", Left = ctrlX + 260, Top = y, Width = 100 };
        btnLoadFields.Click += BtnLoadFields_Click;
        Controls.Add(btnLoadFields);
        y += rowH;

        // Ausgabeordner
        MakeLabel("Ausgabeordner:", y);
        txtOutputRoot = new TextBox { Left = ctrlX, Top = y, Width = ctrlW };
        Controls.Add(txtOutputRoot); y += rowH;

        // Status-DB
        MakeLabel("Status-DB (SQLite):", y);
        txtStateDb = new TextBox { Left = ctrlX, Top = y, Width = ctrlW };
        Controls.Add(txtStateDb); y += rowH;

        // Hash-Tiefe
        MakeLabel("Hash-Unterordner (0-2):", y);
        numHashDepth = new NumericUpDown { Left = ctrlX, Top = y, Width = 80, Minimum = 0, Maximum = 2 };
        Controls.Add(numHashDepth); y += rowH;

        // PageSize
        MakeLabel("Seitengröße:", y);
        numPageSize = new NumericUpDown { Left = ctrlX, Top = y, Width = 100, Minimum = 1, Maximum = 5000 };
        Controls.Add(numPageSize); y += rowH;

        // Delay
        MakeLabel("Verzögerung (ms):", y);
        numDelay = new NumericUpDown { Left = ctrlX, Top = y, Width = 100, Minimum = 0, Maximum = 60000 };
        Controls.Add(numDelay); y += rowH;

        // Retries
        MakeLabel("Max. Wiederholungen:", y);
        numRetries = new NumericUpDown { Left = ctrlX, Top = y, Width = 80, Minimum = 0, Maximum = 20 };
        Controls.Add(numRetries); y += rowH;

        // Rescan
        MakeLabel("Nachscannen (Min, 0=einmal):", y);
        numRescan = new NumericUpDown { Left = ctrlX, Top = y, Width = 100, Minimum = 0, Maximum = 100000 };
        Controls.Add(numRescan); y += rowH;

        // DownloadPerSection
        chkPerSection = new CheckBox { Text = "Pro Sektion herunterladen", Left = ctrlX, Top = y, Width = 250 };
        Controls.Add(chkPerSection); y += rowH;

        // Speichern-Button
        btnSave = new Button { Text = "Speichern", Left = ctrlX, Top = y, Width = 120 };
        btnSave.Click += BtnSave_Click;
        Controls.Add(btnSave); y += rowH + 10;

        // --- Dienststeuerung ---
        var sep = new Label
        {
            Text = "Dienststeuerung",
            Left = 12, Top = y, Width = 540,
            BorderStyle = BorderStyle.None,
            Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold)
        };
        Controls.Add(sep); y += 25;

        btnSvcInstall = new Button { Text = "Dienst installieren", Left = 12, Top = y, Width = 130 };
        btnSvcInstall.Click += BtnSvcInstall_Click;
        Controls.Add(btnSvcInstall);

        btnSvcStart = new Button { Text = "Dienst starten", Left = 150, Top = y, Width = 120 };
        btnSvcStart.Click += BtnSvcStart_Click;
        Controls.Add(btnSvcStart);

        btnSvcStop = new Button { Text = "Dienst stoppen", Left = 278, Top = y, Width = 120 };
        btnSvcStop.Click += BtnSvcStop_Click;
        Controls.Add(btnSvcStop);

        btnSvcUninstall = new Button { Text = "Dienst deinstallieren", Left = 406, Top = y, Width = 140 };
        btnSvcUninstall.Click += BtnSvcUninstall_Click;
        Controls.Add(btnSvcUninstall);
        y += rowH + 5;

        lblServiceStatus = new Label { Text = "Dienststatus: -", Left = 12, Top = y, Width = 260 };
        Controls.Add(lblServiceStatus);
        lblProgress = new Label { Text = "Fortschritt: -", Left = 290, Top = y, Width = 260 };
        Controls.Add(lblProgress);
        y += rowH;

        // --- Log ---
        var lblLog = new Label { Text = "Meldungen:", Left = 12, Top = y, Width = 120 };
        Controls.Add(lblLog); y += 22;

        txtLog = new TextBox
        {
            Left = 12, Top = y, Width = 560, Height = 160,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };
        Controls.Add(txtLog);
        y += 170;

        // Status-Timer (pollt Dienststatus + Fortschritt).
        statusTimer = new System.Windows.Forms.Timer(components) { Interval = 2000 };
        statusTimer.Tick += StatusTimer_Tick;

        // --- Formular ---
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(590, y + 15);
        Text = "DwDocExport – DocuWare Dokument-Export";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new System.Drawing.Size(606, 500);
    }
}
