using System;
using System.Drawing;
using System.Windows.Forms;

namespace DwDocExport;

/// <summary>Kleiner, eigenständiger Texteingabe-Dialog (Ersatz für eine InputBox).</summary>
internal static class Prompt
{
    public static string? Text(IWin32Window owner, string title, string label, string defaultValue = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(400, 130),
            BackColor = Theme.Bg
        };

        var lbl = new Label { Left = 14, Top = 16, Width = 372, Text = label, ForeColor = Theme.Text };
        var box = new TextBox { Left = 14, Top = 44, Width = 372, Text = defaultValue, BackColor = Theme.InputBg, ForeColor = Theme.Text };
        var ok = new Button { Text = "OK", Left = 214, Top = 84, Width = 80, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Abbrechen", Left = 304, Top = 84, Width = 82, DialogResult = DialogResult.Cancel };
        Theme.Primary(ok);
        Theme.Secondary(cancel);

        form.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}
