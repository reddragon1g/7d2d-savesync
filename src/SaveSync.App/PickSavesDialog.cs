using SaveSync.Core;

namespace SaveSync.App;

/// <summary>
/// One offered save, flattened so the picker works for a USB stick and the network alike. Both
/// paths ask the same question, so both get the same screen rather than two that can drift apart.
/// </summary>
public sealed record PickItem(string SaveName, string World, string Reason, long Bytes, bool Safe, object Source)
{
    public string Display => $"{SaveName} ({World})";
}

/// <summary>
/// Which of several saves to bring over.
///
/// The two big buttons deliberately hide per-save decisions, and for one save that is right. For
/// three it is not: "put the saves on this PC" is all-or-nothing, so a single save that needs a
/// decision holds up the two that do not. This screen exists only when there is genuinely a choice
/// to make, and it pre-ticks exactly the ones that are safe on their own - so the common answer is
/// still one click, and the save that needs thinking about is the only one left unticked.
/// </summary>
public sealed class PickSavesDialog : Form
{
    private const int PadX = 26;
    private const int Width_ = 720;

    private readonly List<(PickItem Item, SaveRow Row)> _rows = new();
    private readonly FlatButton _go = new("Bring these over", primary: true) { Width = 210, Height = 42 };
    private readonly FlatButton _cancel = new("Cancel") { Width = 120, Height = 42 };
    private readonly Label _tally = new();

    public PickSavesDialog(IReadOnlyList<PickItem> choices)
    {
        Text = "Which saves?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;

        int inner = Width_ - PadX * 2;
        int y = 22;

        var heading = new Label
        {
            Font = Theme.Display,
            ForeColor = Theme.Text,
            AutoSize = false,
            Text = "Which saves do you want on this PC?",
        };
        heading.SetBounds(PadX, y, inner, 30);
        Controls.Add(heading);
        y += 34;

        var sub = new Label
        {
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            AutoSize = false,
            Text = "The ones that are safe to bring over are already ticked. Anything that needs you to "
                   + "decide something is left unticked, and you can do it separately afterwards.",
        };
        sub.SetBounds(PadX, y, inner, 34);
        Controls.Add(sub);
        y += 42;

        foreach (var choice in choices)
        {
            var row = new SaveRow(choice) { Checked = choice.Safe };
            row.SetBounds(PadX, y, inner, SaveRow.FixedHeight);
            row.Toggled += (_, _) => UpdateTally();
            Controls.Add(row);
            _rows.Add((choice, row));
            y += SaveRow.FixedHeight + 8;
        }

        y += 6;

        _tally.Font = Theme.Small;
        _tally.ForeColor = Theme.Faint;
        _tally.AutoSize = false;
        _tally.SetBounds(PadX, y + 12, inner - 350, 20);
        Controls.Add(_tally);

        _go.SetBounds(Width_ - PadX - _go.Width, y, _go.Width, _go.Height);
        _go.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        Controls.Add(_go);

        _cancel.SetBounds(_go.Left - 12 - _cancel.Width, y, _cancel.Width, _cancel.Height);
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(_cancel);

        ClientSize = new Size(Width_, y + _go.Height + 22);
        AcceptButton = _go;
        CancelButton = _cancel;

        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);
        UpdateTally();
    }

    /// <summary>The saves the user ticked, in the order they were shown.</summary>
    public List<PickItem> Chosen => _rows.Where(r => r.Row.Checked).Select(r => r.Item).ToList();

    /// <summary>The ticked saves' original objects, cast back to whatever the caller put in.</summary>
    public List<T> ChosenAs<T>() => Chosen.Select(c => c.Source).OfType<T>().ToList();

    private void UpdateTally()
    {
        var picked = _rows.Where(r => r.Row.Checked).ToList();
        long bytes = picked.Sum(r => r.Item.Bytes);

        _go.Enabled = picked.Count > 0;
        _tally.Text = picked.Count == 0
            ? "Nothing ticked."
            : $"{picked.Count} of {_rows.Count} selected{Theme.Dot}{PathUtil.HumanBytes(bytes)}";
    }
}

/// <summary>One tickable save: what it is, how big, and what will happen to it.</summary>
internal sealed class SaveRow : Panel
{
    public const int FixedHeight = 62;

    private readonly string _title;
    private readonly string _detail;
    private readonly bool _safe;
    private bool _checked;
    private bool _hover;

    public SaveRow(PickItem item)
    {
        bool safe = item.Safe;
        // World as well as name, always: two saves called "My Game" in different worlds is exactly
        // the situation where a list of bare names is useless.
        _title = $"{item.SaveName}   ({item.World})";
        _detail = safe
            ? $"{item.Reason}{Theme.Dot}{PathUtil.HumanBytes(item.Bytes)}"
            : $"{item.Reason}{Theme.Dot}{PathUtil.HumanBytes(item.Bytes)}{Theme.Dot}will ask you first";
        _safe = safe;

        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        BackColor = Theme.Background;
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
    }

    public event EventHandler? Toggled;

    public bool Checked
    {
        get => _checked;
        set { _checked = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        Checked = !Checked;
        Toggled?.Invoke(this, EventArgs.Empty);
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            Checked = !Checked;
            Toggled?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var body = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var bg = new SolidBrush(_checked ? Theme.SurfaceRaised : _hover ? Theme.SurfaceHover : Theme.Surface))
            g.FillRectangle(bg, body);
        using (var pen = new Pen(_checked ? Theme.Accent : Theme.Border))
            g.DrawRectangle(pen, body);

        // The tick box.
        var box = new Rectangle(16, (Height - 18) / 2, 18, 18);
        using (var boxBg = new SolidBrush(_checked ? Theme.Accent : Theme.Background))
            g.FillRectangle(boxBg, box);
        using (var pen = new Pen(_checked ? Theme.Accent : Theme.BorderStrong))
            g.DrawRectangle(pen, box);

        if (_checked)
        {
            using var tick = new Pen(Color.White, 2f);
            g.DrawLines(tick, new[]
            {
                new Point(box.Left + 4, box.Top + 9),
                new Point(box.Left + 7, box.Top + 12),
                new Point(box.Left + 14, box.Top + 5),
            });
        }

        int textLeft = box.Right + 14;
        TextRenderer.DrawText(g, _title, Theme.BodyBold,
            new Rectangle(textLeft, 12, Width - textLeft - 14, 20),
            Theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(g, _detail, Theme.Small,
            new Rectangle(textLeft, 33, Width - textLeft - 14, 18),
            _safe ? Theme.Muted : Theme.Warn, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        if (Focused)
        {
            using var focus = new Pen(Theme.BorderStrong) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawRectangle(focus, 2, 2, Width - 5, Height - 5);
        }
    }
}
