using System.Drawing.Drawing2D;
using SaveSync.Core;

namespace SaveSync.App;

/// <summary>
/// Message and confirmation boxes, drawn to match the rest of the app.
///
/// The stock Windows message box is painted by the system in light colours and cannot be
/// restyled, so every one of them flashed white in the middle of a dark app. They also default
/// their accept button to "Yes", which is the wrong default for anything destructive.
/// </summary>
public static class Dialogs
{
    public static void Info(IWin32Window? owner, string title, string message)
        => Show(owner, Severity.Info, title, message, "OK", null, false);

    public static void Warn(IWin32Window? owner, string title, string message)
        => Show(owner, Severity.Warning, title, message, "OK", null, false);

    public static void Error(IWin32Window? owner, string title, string message)
        => Show(owner, Severity.Blocker, title, message, "OK", null, false);

    /// <summary>
    /// Returns true only on an explicit yes. The cancel side is always the default, so a stray
    /// Enter or Escape never commits anything.
    /// </summary>
    public static bool Confirm(
        IWin32Window? owner, string title, string message,
        string yesText, string noText = "Cancel", bool dangerous = false)
        => Show(owner, dangerous ? Severity.Warning : Severity.Info, title, message, yesText, noText, dangerous);

    private static bool Show(
        IWin32Window? owner, Severity level, string title, string message,
        string primaryText, string? secondaryText, bool dangerous)
    {
        const int width = 520;
        const int padX = 26;
        int inner = width - padX * 2;

        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            ClientSize = new Size(width, 200),
        };

        var accent = Theme.Strong(level);

        var head = new Label
        {
            Font = Theme.Title,
            ForeColor = accent,
            AutoSize = false,
            Text = title,
        };
        head.SetBounds(padX, 22, inner, 28);
        form.Controls.Add(head);

        int bodyHeight = Banner.MeasureHeight(message, Theme.Body, inner);
        var body = new Label
        {
            Font = Theme.Body,
            ForeColor = Theme.Text,
            AutoSize = false,
            Text = message,
        };
        body.SetBounds(padX, 56, inner, bodyHeight);
        form.Controls.Add(body);

        int y = 56 + bodyHeight + 24;

        bool result = false;

        var primary = new FlatButton(primaryText, primary: true) { Width = 0, Height = 40 };
        primary.Width = Math.Max(120, TextRenderer.MeasureText(primaryText, Theme.ButtonFont).Width + 44);
        primary.SetBounds(width - padX - primary.Width, y, primary.Width, 40);
        primary.Click += (_, _) => { result = true; form.DialogResult = DialogResult.OK; form.Close(); };
        form.Controls.Add(primary);

        if (secondaryText is not null)
        {
            var secondary = new FlatButton(secondaryText) { Height = 40 };
            secondary.Width = Math.Max(110, TextRenderer.MeasureText(secondaryText, Theme.ButtonFont).Width + 40);
            secondary.SetBounds(primary.Left - 12 - secondary.Width, y, secondary.Width, 40);
            secondary.Click += (_, _) => { result = false; form.DialogResult = DialogResult.Cancel; form.Close(); };
            form.Controls.Add(secondary);

            // Escape cancels; Enter does nothing on a destructive prompt.
            form.CancelButton = secondary;
            form.AcceptButton = dangerous ? null : primary;
            secondary.Select();
        }
        else
        {
            form.AcceptButton = primary;
            form.CancelButton = primary;
        }

        form.ClientSize = new Size(width, y + 40 + 22);

        // A coloured rule under the heading, so the severity reads without relying on colour alone
        // being noticed - the heading text already says what happened.
        form.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(accent);
            e.Graphics.FillRectangle(brush, 0, 0, form.ClientSize.Width, 3);
        };

        form.HandleCreated += (_, _) => Theme.UseDarkTitleBar(form);
        form.ShowDialog(owner);
        return result;
    }
}
