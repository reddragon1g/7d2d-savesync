using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using SaveSync.Core;

namespace SaveSync.App;

/// <summary>
/// Dark palette drawn from the game itself: warm near-black, weathered browns, and the burnt
/// orange the game uses for everything important. Contrast is kept well above the point of
/// legibility rather than at it, because this gets used late at night by people who are not
/// looking for subtlety.
///
/// No text ever relies on colour alone to carry its meaning.
/// </summary>
public static class Theme
{
    // Surfaces, darkest first.
    public static readonly Color Background = Color.FromArgb(20, 18, 15);
    public static readonly Color Surface = Color.FromArgb(30, 27, 22);
    public static readonly Color SurfaceRaised = Color.FromArgb(38, 34, 27);
    public static readonly Color SurfaceHover = Color.FromArgb(48, 43, 34);

    public static readonly Color Border = Color.FromArgb(58, 52, 42);
    public static readonly Color BorderStrong = Color.FromArgb(82, 73, 58);

    public static readonly Color Text = Color.FromArgb(237, 230, 216);
    public static readonly Color Muted = Color.FromArgb(155, 146, 133);
    public static readonly Color Faint = Color.FromArgb(118, 110, 99);

    // Burnt orange: the game's signature, and this tool's primary action colour.
    public static readonly Color Accent = Color.FromArgb(194, 87, 30);
    public static readonly Color AccentHover = Color.FromArgb(219, 106, 40);
    public static readonly Color AccentSoft = Color.FromArgb(46, 32, 22);

    public static readonly Color Good = Color.FromArgb(138, 176, 74);
    public static readonly Color GoodSoft = Color.FromArgb(31, 38, 23);

    public static readonly Color Warn = Color.FromArgb(216, 162, 43);
    public static readonly Color WarnSoft = Color.FromArgb(45, 37, 20);

    public static readonly Color Danger = Color.FromArgb(190, 68, 55);
    public static readonly Color DangerSoft = Color.FromArgb(48, 26, 24);

    private static readonly string HeadFamily = FirstAvailable("Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold", "Segoe UI");
    private static readonly string BodyFamily = FirstAvailable("Segoe UI", "Tahoma");

    public static Font Display { get; } = new(HeadFamily, 17f, FontStyle.Regular);
    public static Font Title { get; } = new(HeadFamily, 13.5f, FontStyle.Regular);
    public static Font Heading { get; } = new(BodyFamily, 11.5f, FontStyle.Bold);
    public static Font Body { get; } = new(BodyFamily, 10f);
    public static Font BodyBold { get; } = new(BodyFamily, 10f, FontStyle.Bold);
    public static Font Small { get; } = new(BodyFamily, 8.75f);
    public static Font SmallBold { get; } = new(BodyFamily, 8.75f, FontStyle.Bold);
    public static Font ButtonFont { get; } = new(BodyFamily, 10f, FontStyle.Bold);

    /// <summary>
    /// Middle dot separator, built from its code point so the source file stays pure ASCII.
    /// A C# file without a byte-order mark is compiled using the machine's ANSI codepage, so a
    /// literal here renders as mojibake on some machines and not others - a bug that only shows
    /// up after shipping, on somebody else's PC.
    /// </summary>
    public static readonly string Dot = "  " + (char)0x00B7 + "  ";

    private static string FirstAvailable(params string[] families)
    {
        using var installed = new InstalledFontCollection();
        var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in families)
            if (names.Contains(f)) return f;
        return families[^1];
    }

    public static void RoundRect(Graphics g, Rectangle r, int radius, Color fill, Color? border = null)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = RoundedPath(r, radius);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (border is not null)
        {
            using var p = new Pen(border.Value);
            g.DrawPath(p, path);
        }
    }

    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0) { path.AddRectangle(r); return path; }

        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Soft(Severity level) => level switch
    {
        Severity.Blocker => DangerSoft,
        Severity.Warning => WarnSoft,
        _ => AccentSoft,
    };

    public static Color Strong(Severity level) => level switch
    {
        Severity.Blocker => Danger,
        Severity.Warning => Warn,
        _ => Accent,
    };

    /// <summary>Relative time in words. "2 hours ago" is read; a raw timestamp has to be decoded.</summary>
    public static string Ago(DateTimeOffset when)
    {
        if (when == DateTimeOffset.MinValue) return "never";

        var span = DateTimeOffset.UtcNow - when.ToUniversalTime();
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        if (span.TotalMinutes < 2) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} minutes ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hour{Plural((int)span.TotalHours)} ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} day{Plural((int)span.TotalDays)} ago";

        return when.ToLocalTime().ToString("ddd d MMM");
    }

    public static string Plural(int n) => n == 1 ? "" : "s";

    private static Icon? _appIcon;

    /// <summary>
    /// The tray and window icon, drawn at runtime rather than shipped as a file, so the whole
    /// program stays a single executable that can be copied onto a stick.
    /// </summary>
    public static Icon AppIcon
    {
        get
        {
            if (_appIcon is not null) return _appIcon;

            using var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (var back = new SolidBrush(Accent))
                using (var path = RoundedPath(new Rectangle(0, 0, 31, 31), 7))
                    g.FillPath(back, path);

                // Two arrows, one each way: this thing moves saves in both directions.
                using var pen = new Pen(Color.FromArgb(255, 248, 240), 3f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor,
                };
                g.DrawLine(pen, 8, 12, 23, 12);
                g.DrawLine(pen, 24, 20, 9, 20);
            }

            _appIcon = Icon.FromHandle(bitmap.GetHicon());
            return _appIcon;
        }
    }

    /// <summary>Windows 11 keeps the title bar light unless told otherwise; a light bar on a dark app looks broken.</summary>
    public static void UseDarkTitleBar(IWin32Window window)
    {
        try
        {
            int on = 1;
            // 20 on current builds, 19 on early Windows 10 2004. Try both, ignore failure.
            if (DwmSetWindowAttribute(window.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(window.Handle, 19, ref on, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// <summary>A flat button that reads clearly in all four states and never hides why it is disabled.</summary>
public sealed class FlatButton : Button
{
    private bool _hover;
    private bool _down;

    public FlatButton(string text, bool primary = false)
    {
        Text = text;
        Primary = primary;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.ButtonFont;
        Cursor = Cursors.Hand;
        AutoSize = false;
        Height = 38;
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public bool Primary { get; set; }

    /// <summary>Shown beside the button when disabled, so greyed-out is never a mystery.</summary>
    public string DisabledReason { get; set; } = "";

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Surface);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        Color fill, fore, border;
        if (!Enabled)
        {
            fill = Theme.Surface;
            fore = Theme.Faint;
            border = Theme.Border;
        }
        else if (Primary)
        {
            fill = _down ? Color.FromArgb(168, 74, 24) : _hover ? Theme.AccentHover : Theme.Accent;
            fore = Color.FromArgb(255, 248, 240);
            border = fill;
        }
        else
        {
            fill = _down ? Theme.Surface : _hover ? Theme.SurfaceHover : Theme.SurfaceRaised;
            fore = Theme.Text;
            border = _hover ? Theme.BorderStrong : Theme.Border;
        }

        Theme.RoundRect(g, r, 5, fill, border);

        TextRenderer.DrawText(g, Text, Font, r, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (Focused && Enabled)
        {
            var inner = Rectangle.Inflate(r, -3, -3);
            using var p = new Pen(Color.FromArgb(120, fore)) { DashStyle = DashStyle.Dot };
            g.DrawPath(p, Theme.RoundedPath(inner, 4));
        }
    }
}

/// <summary>A coloured notice block. Wording first, colour as reinforcement.</summary>
public sealed class Banner : Panel
{
    private readonly Label _headline = new();
    private readonly Label _body = new();
    private readonly List<FlatButton> _actions = new();

    public Banner(Severity level, string? headline, string body, params (string Text, Action OnClick)[] actions)
        : this(level, null, null, headline, body, actions) { }

    /// <summary>Colour override, for states the Severity scale has no word for - such as "this is safe".</summary>
    public Banner(Severity level, Color? accent, Color? soft, string? headline, string body,
        params (string Text, Action OnClick)[] actions)
    {
        Level = level;
        _accent = accent ?? Theme.Strong(level);
        DoubleBuffered = true;
        BackColor = soft ?? Theme.Soft(level);

        _headline.Font = Theme.BodyBold;
        _headline.ForeColor = _accent;
        _headline.AutoSize = false;
        _headline.Text = headline ?? "";
        _headline.Visible = headline is not null;

        _body.Font = Theme.Body;
        _body.ForeColor = Theme.Text;
        _body.AutoSize = false;
        _body.Text = body;

        Controls.Add(_headline);
        Controls.Add(_body);

        foreach (var (text, onClick) in actions)
        {
            // Sized to the label, not a guess: "Allow over the network" was being cut to "Allow over the net...".
            int width = Math.Max(150, TextRenderer.MeasureText(text, Theme.ButtonFont).Width + 40);
            var b = new FlatButton(text, primary: true) { Width = width, Height = 34 };
            b.Click += (_, _) => onClick();
            _actions.Add(b);
            Controls.Add(b);
        }

        Relayout();
    }

    private readonly Color _accent;

    public Severity Level { get; }
    public Color Accent => _accent;

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
    }

    private void Relayout()
    {
        const int padX = 18;
        int w = Math.Max(120, Width - padX * 2);
        int y = 14;

        if (_headline.Visible)
        {
            _headline.SetBounds(padX, y, w, 20);
            y += 24;
        }

        int bodyHeight = MeasureHeight(_body.Text, Theme.Body, w);
        _body.SetBounds(padX, y, w, bodyHeight);
        y += bodyHeight + 12;

        int x = padX;
        foreach (var b in _actions)
        {
            b.SetBounds(x, y, b.Width, b.Height);
            x += b.Width + 10;
        }
        if (_actions.Count > 0) y += _actions[0].Height + 4;

        Height = y + 10;
    }

    internal static int MeasureHeight(string text, Font font, int width)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var size = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak);
        return Math.Max(18, size.Height + 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.RoundRect(g, r, 6, BackColor, Color.FromArgb(110, Accent));
        using var bar = new SolidBrush(Accent);
        g.FillRectangle(bar, 1, 6, 3, Math.Max(0, Height - 12));
    }
}

/// <summary>
/// A scrolling stack that does not jump when a child takes focus.
///
/// WinForms scrolls a focused control into view automatically. Because the action buttons sit at
/// the bottom of each card, the first card's title was being scrolled off the top of the window
/// the moment the form opened.
/// </summary>
public sealed class StackPanel : FlowLayoutPanel
{
    public StackPanel()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
    }

    protected override Point ScrollToControl(Control activeControl)
        => AutoScrollPosition;

    public void ScrollToTop()
    {
        AutoScrollPosition = new Point(0, 0);
        PerformLayout();
    }
}

/// <summary>Slim progress bar. The stock control cannot be themed dark, so it is drawn here.</summary>
public sealed class ThinProgress : Control
{
    private double _value;

    public ThinProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Height = 8;
    }

    /// <summary>0 to 1. Values outside that range are clamped rather than throwing.</summary>
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Surface);

        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.RoundRect(g, track, Height / 2, Theme.SurfaceRaised, Theme.Border);

        int w = (int)((Width - 2) * _value);
        if (w > 2)
            Theme.RoundRect(g, new Rectangle(1, 1, w, Height - 3), (Height - 3) / 2, Theme.Accent);
    }
}
