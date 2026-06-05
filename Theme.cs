using System;
using System.Drawing;
using System.Windows.Forms;

namespace DwDocExport;

/// <summary>
/// Zentrale Farb-, Schrift- und Stil-Definitionen für ein modernes, flaches
/// Erscheinungsbild der WinForms-Oberfläche.
/// </summary>
internal static class Theme
{
    // Farbpalette (an ein modernes, helles Design angelehnt).
    public static readonly Color Bg = Color.FromArgb(245, 246, 248);
    public static readonly Color Card = Color.White;
    public static readonly Color Accent = Color.FromArgb(0, 103, 192);
    public static readonly Color AccentHover = Color.FromArgb(0, 86, 163);
    public static readonly Color AccentPressed = Color.FromArgb(0, 72, 138);
    public static readonly Color Text = Color.FromArgb(28, 28, 30);
    public static readonly Color Subtle = Color.FromArgb(110, 112, 116);
    public static readonly Color Border = Color.FromArgb(210, 213, 218);
    public static readonly Color SecondaryBg = Color.FromArgb(238, 240, 243);
    public static readonly Color SecondaryHover = Color.FromArgb(226, 229, 233);
    public static readonly Color Success = Color.FromArgb(16, 124, 16);
    public static readonly Color Danger = Color.FromArgb(176, 42, 42);

    public static Font Base() => new("Segoe UI", 9.75F, FontStyle.Regular);
    public static Font Title() => new("Segoe UI Semibold", 15.5F, FontStyle.Regular);
    public static Font Subtitle() => new("Segoe UI", 9.5F, FontStyle.Regular);
    public static Font Section() => new("Segoe UI Semibold", 10.5F, FontStyle.Regular);

    /// <summary>Gestaltet einen Button als gefüllte Akzent-Schaltfläche (Primäraktion).</summary>
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

    /// <summary>Gestaltet einen Button als unauffällige, helle Schaltfläche (Sekundäraktion).</summary>
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

    /// <summary>Erzeugt eine „Karte" – ein weißes Panel mit dünnem Rahmen für Gruppen.</summary>
    public static Panel Card(int left, int top, int width, int height)
    {
        return new Panel
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            BackColor = Card,
            BorderStyle = BorderStyle.FixedSingle
        };
    }
}
