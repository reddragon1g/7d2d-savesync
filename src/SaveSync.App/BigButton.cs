using System.Drawing.Drawing2D;

namespace SaveSync.App;

/// <summary>
/// One of the two actions on the main screen.
///
/// Deliberately large, with a second line that says what pressing it will actually do to how many
/// saves. When an action is not available it stays in place and greyed rather than disappearing, so
/// the screen never changes shape and the reason is always readable.
/// </summary>
public sealed class BigButton : Control
{
    private bool _hover;
    private bool _down;

    public BigButton(string title)
    {
        Title = title;
        Height = 84;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
    }

    public string Title { get; set; }

    /// <summary>What will happen, in the user's words. Shown under the title.</summary>
    public string Subtitle { get; set; } = "";

    /// <summary>The one action the tool thinks they want. Only ever set on one button at a time.</summary>
    public bool Recommended { get; set; }

    public void SetState(bool enabled, string subtitle, bool recommended)
    {
        Enabled = enabled;
        Subtitle = subtitle;
        Recommended = recommended;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Enabled && e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Background);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        Color fill, border, titleColor, subColor;
        if (!Enabled)
        {
            fill = Theme.Surface;
            border = Theme.Border;
            titleColor = Theme.Faint;
            subColor = Theme.Faint;
        }
        else if (Recommended)
        {
            fill = _down ? Color.FromArgb(168, 74, 24) : _hover ? Theme.AccentHover : Theme.Accent;
            border = fill;
            titleColor = Color.FromArgb(255, 250, 244);
            subColor = Color.FromArgb(255, 226, 204);
        }
        else
        {
            fill = _down ? Theme.Surface : _hover ? Theme.SurfaceHover : Theme.SurfaceRaised;
            border = _hover ? Theme.BorderStrong : Theme.Border;
            titleColor = Theme.Text;
            subColor = Theme.Muted;
        }

        Theme.RoundRect(g, r, 8, fill, border);

        var titleRect = new Rectangle(28, 18, Width - 56, 30);
        TextRenderer.DrawText(g, Title, Theme.Title, titleRect, titleColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var subRect = new Rectangle(28, 48, Width - 56, 22);
        TextRenderer.DrawText(g, Subtitle, Theme.Body, subRect, subColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (Focused && Enabled)
        {
            using var p = new Pen(Color.FromArgb(130, titleColor)) { DashStyle = DashStyle.Dot };
            g.DrawPath(p, Theme.RoundedPath(Rectangle.Inflate(r, -4, -4), 6));
        }
    }
}
