using System;
using System.Drawing;
using System.Windows.Forms;

namespace DwDocExport;

/// <summary>
/// Zentrale Farb-/Schrift-/Stil-Definitionen mit Hell- und Dunkel-Variante.
/// <see cref="SetDark"/> muss vor dem Aufbau der Oberfläche aufgerufen werden.
/// </summary>
internal static class Theme
{
    private static bool _dark;
    public static void SetDark(bool dark) => _dark = dark;
    public static bool IsDark => _dark;

    public static Color Accent => Color.FromArgb(0, 103, 192);
    public static Color AccentHover => Color.FromArgb(0, 86, 163);
    public static Color AccentPressed => Color.FromArgb(0, 72, 138);
    public static Color Success => Color.FromArgb(_dark ? 90 : 16, _dark ? 200 : 124, _dark ? 90 : 16);
    public static Color Danger => Color.FromArgb(_dark ? 240 : 176, _dark ? 90 : 42, _dark ? 90 : 42);

    public static Color Bg => _dark ? Color.FromArgb(30, 31, 34) : Color.FromArgb(245, 246, 248);
    public static Color Card => _dark ? Color.FromArgb(43, 45, 49) : Color.White;
    public static Color Text => _dark ? Color.FromArgb(230, 230, 230) : Color.FromArgb(28, 28, 30);
    public static Color Subtle => _dark ? Color.FromArgb(160, 163, 168) : Color.FromArgb(110, 112, 116);
    public static Color Border => _dark ? Color.FromArgb(58, 61, 66) : Color.FromArgb(210, 213, 218);
    public static Color InputBg => _dark ? Color.FromArgb(27, 28, 31) : Color.White;
    public static Color SecondaryBg => _dark ? Color.FromArgb(55, 58, 63) : Color.FromArgb(238, 240, 243);
    public static Color SecondaryHover => _dark ? Color.FromArgb(66, 69, 75) : Color.FromArgb(226, 229, 233);

    public static Font Base() => new("Segoe UI", 9.75F, FontStyle.Regular);
    public static Font Title() => new("Segoe UI Semibold", 15.5F, FontStyle.Regular);
    public static Font Subtitle() => new("Segoe UI", 9.5F, FontStyle.Regular);
    public static Font Section() => new("Segoe UI Semibold", 10.5F, FontStyle.Regular);

    public static void Primary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI", 9.75F, FontStyle.Regular);
        b.Cursor = Cursors.Hand;
        b.Height = 32;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.FlatAppearance.MouseDownBackColor = AccentPressed;
    }

    public static void Secondary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Border;
        b.BackColor = SecondaryBg;
        b.ForeColor = Text;
        b.Font = new Font("Segoe UI", 9.75F, FontStyle.Regular);
        b.Cursor = Cursors.Hand;
        b.Height = 32;
        b.FlatAppearance.MouseOverBackColor = SecondaryHover;
        b.FlatAppearance.MouseDownBackColor = Border;
    }

    public static Panel Card(int left, int top, int width, int height) => new()
    {
        Left = left, Top = top, Width = width, Height = height,
        BackColor = Card, BorderStyle = BorderStyle.FixedSingle
    };

    /// <summary>Wendet die Farben (v. a. im Dunkelmodus) rekursiv auf Steuerelemente an.</summary>
    public static void Apply(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox tb:
                    tb.BackColor = InputBg; tb.ForeColor = Text; break;
                case ComboBox cb:
                    cb.BackColor = InputBg; cb.ForeColor = Text; break;
                case NumericUpDown nud:
                    nud.BackColor = InputBg; nud.ForeColor = Text; break;
                case Label lbl:
                    lbl.ForeColor = Text; lbl.BackColor = Color.Transparent; break;
                case CheckBox chk:
                    chk.ForeColor = Text; break;
                case Panel p when p.BackColor != Accent:
                    p.BackColor = p.BorderStyle == BorderStyle.FixedSingle ? Card : Bg; break;
                case TabPage tp:
                    tp.BackColor = Bg; break;
            }
            if (c.HasChildren)
                Apply(c);
        }
    }

    /// <summary>Setzt die PropertyGrid-Farben passend zum Design.</summary>
    public static void ApplyToGrid(PropertyGrid grid)
    {
        grid.BackColor = Bg;
        grid.ViewBackColor = InputBg;
        grid.ViewForeColor = Text;
        grid.LineColor = Border;
        grid.CategoryForeColor = Accent;
        grid.HelpBackColor = Card;
        grid.HelpForeColor = Text;
        grid.CategorySplitterColor = Border;
    }
}
