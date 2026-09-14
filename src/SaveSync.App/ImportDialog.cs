using System.Drawing.Drawing2D;
using SaveSync.Core;

namespace SaveSync.App;

/// <summary>
/// The decision screen, and the one place where progress can actually be lost if the design is
/// sloppy.
///
/// Two rules shape it. When the transfer is provably safe the user gets one button and no choice
/// to get wrong. When it is not safe there is no default, no primary button, and no Enter-key
/// shortcut until they pick a side deliberately - and whichever side loses is kept as a restorable
/// backup either way.
/// </summary>
public sealed class ImportDialog : Form
{
    private const int PadX = 26;
    private const int Width_ = 760;

    private readonly ImportPlan _plan;
    private readonly ChoiceRow? _takeIncoming;
    private readonly ChoiceRow? _keepLocal;
    private readonly ChoiceRow? _keepBoth;
    private readonly TextBox? _newName;
    private readonly FlatButton _go;
    private readonly FlatButton _cancel = new("Cancel");
    private readonly bool _mustChoose;

    public ImportDialog(ImportPlan plan)
    {
        _plan = plan;
        _mustChoose = plan.NeedsHumanChoice || plan.PreviouslyInstalledAt is not null;

        Text = "Install save";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ClientSize = new Size(Width_, 400);

        var incoming = plan.Info.Passport;
        int inner = Width_ - PadX * 2;
        int y = 22;

        Add(new Label
        {
            Font = Theme.Display,
            ForeColor = Theme.Text,
            AutoSize = false,
            Text = $"{incoming.SaveName}  ({incoming.World})",
        }, y, 30, inner);
        y += 32;

        Add(new Label
        {
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            AutoSize = false,
            Text = string.Join(Theme.Dot,
                $"From {Friendly(plan.Info.CreatedBy)}",
                $"packaged {Theme.Ago(plan.Info.CreatedAt)}",
                PathUtil.HumanBytes(plan.Info.PayloadBytes)),
        }, y, 18, inner);
        y += 28;

        bool safe = Lineage.IsSafeToApply(plan.Relation) && !plan.HasBlockers && !_mustChoose;
        y = AddBanner(y, LevelFor(plan), HeadlineFor(plan), Lineage.Explain(plan.Relation),
            safe ? Theme.Good : null, safe ? Theme.GoodSoft : null);

        if (plan.Kinship?.ProvenDifferent == true)
        {
            y = AddBanner(y, Severity.Warning, "These are two different games",
                plan.Kinship.Headline + " "
                + string.Join(" ", plan.Kinship.Reasons)
                + " Keeping both is almost certainly what you want.");
        }
        y = AddComparison(y);

        if (plan.Local is not null
            && !string.Equals(plan.Local.SaveName, incoming.SaveName, StringComparison.OrdinalIgnoreCase))
        {
            y = AddBanner(y, Severity.Warning, "The two saves are not called the same thing",
                $"Coming in: {incoming.SaveName} ({incoming.World}). "
                + $"On this PC: {plan.Local.SaveName} ({plan.Local.World}).");
        }

        foreach (var f in plan.Findings.OrderByDescending(f => f.Severity))
            y = AddBanner(y, f.Severity, null, f.Message);

        if (_mustChoose && !plan.HasBlockers)
        {
            y += 4;

            _takeIncoming = new ChoiceRow(
                $"Use the copy from {Friendly(plan.Info.CreatedBy)}",
                plan.Local is not null
                    ? "Your current save on this PC is kept as a backup you can restore."
                    : "This save does not exist on this PC yet.");
            _takeIncoming.SetBounds(PadX, y, inner, ChoiceRow.FixedHeight);
            Controls.Add(_takeIncoming);
            y += ChoiceRow.FixedHeight + 8;

            _keepLocal = new ChoiceRow(
                "Keep what is already on this PC",
                "Nothing is changed. The copy you brought over is left alone.");
            _keepLocal.SetBounds(PadX, y, inner, ChoiceRow.FixedHeight);
            Controls.Add(_keepLocal);
            y += ChoiceRow.FixedHeight + 8;

            // The way out of a name clash that is not a choice between two games at all. Offered
            // whenever there is something here to clash with, and put first in the eye when the
            // two are provably different games, because then it is almost always the right answer.
            if (plan.Local is not null)
            {
                _keepBoth = new ChoiceRow(
                    "Keep both - install it under a different name  (safest)",
                    "Nothing on this PC is touched at all. The incoming save is added next to it "
                    + "under the name below, and you can delete either one later.");
                _keepBoth.SetBounds(PadX, y, inner, ChoiceRow.FixedHeight);
                Controls.Add(_keepBoth);
                y += ChoiceRow.FixedHeight + 6;

                _newName = new TextBox
                {
                    Font = Theme.Body,
                    BackColor = Theme.Surface,
                    ForeColor = Theme.Text,
                    BorderStyle = BorderStyle.FixedSingle,
                    Text = plan.SuggestedNewName(),
                    Enabled = false,
                };
                _newName.SetBounds(PadX + 34, y, inner - 34, 28);
                Controls.Add(_newName);
                y += 36;
            }

            y += 4;

            // "Keep both" is always the one already chosen, whenever it is available at all.
            //
            // It is the only option that cannot lose anything, so it is the only one safe to put
            // under a pointer that is already moving. It also means nobody has to know in advance
            // whether the other PC has a save by that name - the answer is the same either way,
            // and a spare copy is a tidying-up job rather than a disaster.
            if (_keepBoth is not null) _keepBoth.Checked = true;

            _takeIncoming.Chosen += (_, _) => { _keepLocal.Checked = false; if (_keepBoth is not null) _keepBoth.Checked = false; UpdateGo(); };
            _keepLocal.Chosen += (_, _) => { _takeIncoming.Checked = false; if (_keepBoth is not null) _keepBoth.Checked = false; UpdateGo(); };
            if (_keepBoth is not null)
                _keepBoth.Chosen += (_, _) => { _takeIncoming!.Checked = false; _keepLocal.Checked = false; UpdateGo(); };

            Add(new Label
            {
                Font = Theme.Small,
                ForeColor = Theme.Faint,
                AutoSize = false,
                Text = "Whichever you pick, nothing is deleted. The other copy stays in Backups and can be put back later.",
            }, y, 18, inner);
            y += 26;
        }

        y += 10;

        _go = new FlatButton(_mustChoose ? "Continue" : "Install it", primary: true) { Width = 190, Height = 42 };
        _go.SetBounds(Width_ - PadX - 190, y, 190, 42);
        _go.Click += (_, _) => Finish();

        _cancel.Width = 120;
        _cancel.Height = 42;
        _cancel.SetBounds(_go.Left - 12 - 120, y, 120, 42);
        _cancel.Click += (_, _) => { Choice = ImportChoice.KeepLocal; DialogResult = DialogResult.Cancel; Close(); };

        Controls.Add(_go);
        Controls.Add(_cancel);
        y += 42 + 22;

        ClientSize = new Size(Width_, y);

        // No AcceptButton: Enter must never commit a destructive choice by reflex.
        AcceptButton = null;
        CancelButton = _cancel;

        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);
        UpdateGo();
    }

    public ImportChoice Choice { get; private set; } = ImportChoice.KeepLocal;

    private void Add(Control c, int y, int h, int w)
    {
        c.SetBounds(PadX, y, w, h);
        Controls.Add(c);
    }

    private void UpdateGo()
    {
        if (_plan.HasBlockers)
        {
            _go.Enabled = false;
            _go.Text = "Cannot install";
            _go.DisabledReason = "This transfer is blocked.";
            return;
        }

        if (_mustChoose)
        {
            bool keepBoth = _keepBoth?.Checked ?? false;
            if (_newName is not null) _newName.Enabled = keepBoth;

            bool picked = (_takeIncoming?.Checked ?? false) || (_keepLocal?.Checked ?? false) || keepBoth;
            _go.Enabled = picked && !(keepBoth && string.IsNullOrWhiteSpace(_newName?.Text));

            _go.Text = (_keepLocal?.Checked ?? false) ? "Keep this PC's save"
                     : keepBoth ? "Keep both"
                     : "Continue";
            return;
        }

        _go.Enabled = true;
    }

    private void Finish()
    {
        if (_plan.HasBlockers) return;

        if (_mustChoose)
        {
            if (_keepLocal?.Checked == true)
            {
                Choice = ImportChoice.KeepLocal;
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            if (_keepBoth?.Checked == true)
            {
                var name = (_newName?.Text ?? "").Trim();
                if (name.Length == 0) return;

                // Nothing is replaced, so there is nothing to warn about and nothing to confirm.
                _plan.InstallAsName = name;
                Choice = ImportChoice.InstallAsNewSave;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            if (_takeIncoming?.Checked != true) return;

            if (!Dialogs.Confirm(this, "Please confirm", ConfirmText(),
                    ConfirmButton(), dangerous: true)) return;

            Choice = ImportChoice.TakeIncomingKeepBackup;
        }
        else
        {
            Choice = ImportChoice.Apply;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    // ------------------------------------------------------------------ layout helpers

    private int AddComparison(int y)
    {
        const int gap = 16;
        int w = (Width_ - PadX * 2 - gap) / 2;

        var incoming = _plan.Info.Passport;
        var local = _plan.Local;

        // The day count first, because it is the one thing that tells two identically named saves
        // apart at a glance - a 60-day world and a 32-day world are not a close call once you can
        // see which is which.
        var incomingEvidence = ReadEvidence(PackageLayout.Payload(_plan.PackageDir));
        var localEvidence = local is null ? null : ReadEvidence(local.Folder);

        var left = new SidePanel(
            $"FROM {Friendly(_plan.Info.CreatedBy).ToUpperInvariant()}",
            incomingEvidence?.Readable == true
                ? $"Day {incomingEvidence.Day}  -  version {incoming.Ordinal}"
                : $"Version {incoming.Ordinal}",
            $"Played {Theme.Ago(incoming.LastPlayedAt)}",
            PathUtil.HumanBytes(_plan.Info.PayloadBytes)
                + (incomingEvidence?.PlayerIds.Count > 0 ? $"  -  {incomingEvidence.PlayerIds.Count} players" : ""),
            highlight: Lineage.IsSafeToApply(_plan.Relation));
        left.SetBounds(PadX, y, w, SidePanel.FixedHeight);

        SidePanel right = local is null
            ? new SidePanel("ON THIS PC",
                "Nothing here yet", "This save is not on this PC", "", highlight: false)
            : new SidePanel("ON THIS PC",
                localEvidence?.Readable == true
                    ? $"Day {localEvidence.Day}"
                      + (local.Passport is null ? "  -  never copied" : $"  -  version {local.Passport.Ordinal}")
                    : local.Passport is null ? "Not set up" : $"Version {local.Passport.Ordinal}",
                $"Played {Theme.Ago(local.LastWriteUtc)}",
                PathUtil.HumanBytes(local.SizeBytes)
                    + (localEvidence?.PlayerIds.Count > 0 ? $"  -  {localEvidence.PlayerIds.Count} players" : ""),
                highlight: _plan.Relation == Relation.Stale);
        right.SetBounds(PadX + w + gap, y, w, SidePanel.FixedHeight);

        Controls.Add(left);
        Controls.Add(right);
        return y + SidePanel.FixedHeight + 14;
    }

    /// <summary>
    /// The last thing read before a save is replaced, so it names both sides rather than saying
    /// "your save" and leaving the person to work out which one that is.
    ///
    /// Unrelated gets its own wording because it is the genuinely dangerous case and the one a
    /// generic message hides: the folder about to be taken over is not an older version of the
    /// incoming save at all, it is a different game - quite possibly somebody else's.
    /// </summary>
    private string ConfirmText()
    {
        var nl = Environment.NewLine;
        var para = nl + nl;

        var incoming = _plan.Info.Passport;
        var mine = _plan.Local;

        var comingIn = $"{incoming.SaveName}  ({incoming.World})  from {Friendly(_plan.Info.CreatedBy)}";
        var here = mine is null
            ? "nothing - this save is not on this PC yet"
            : $"{mine.SaveName}  ({mine.World})";

        var sides = $"Coming in:   {comingIn}" + nl + $"On this PC:  {here}";

        return _plan.Relation switch
        {
            Relation.Unrelated =>
                "These are two DIFFERENT saves that happen to share a name." + para
                + sides + para
                + "The save on this PC is NOT an earlier version of the one coming in. It is a "
                + "separate game, and it may well be someone else's." + para
                + "It will be kept as a backup you can put back at any time, but the incoming save "
                + "takes over that folder." + para
                + "Are you sure?",

            Relation.Unregistered =>
                "This PC already has a save in that folder which this program has never seen before." + para
                + sides + para
                + "Because it has never been copied anywhere, there is no way to tell which of the "
                + "two is newer - and it may belong to somebody else." + para
                + "It will be kept as a backup you can put back at any time." + para
                + "Continue?",

            Relation.Stale =>
                "The copy you are installing is OLDER than what is on this PC." + para
                + sides + para
                + "Your current save is kept as a restorable backup, but the game will go back to "
                + "the older state." + para
                + "Are you sure?",

            Relation.Diverged =>
                "Both PCs were played since these last matched, so they cannot be combined." + para
                + sides + para
                + "The copy on this PC is kept as a backup and can be put back at any time." + para
                + "Continue?",

            _ =>
                "The save on this PC will be replaced." + para
                + sides + para
                + "It is kept as a backup you can restore at any time." + para
                + "Continue?",
        };
    }

    private string ConfirmButton() => _plan.Relation switch
    {
        Relation.Unrelated => "Yes, take that folder over",
        Relation.Stale => "Yes, go back to the older one",
        _ => "Yes, replace it",
    };

    private static SaveEvidence? ReadEvidence(string folder)
    {
        try { return SaveEvidence.Read(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private int AddBanner(int y, Severity level, string? headline, string body,
        Color? accent = null, Color? soft = null)
    {
        var banner = new Banner(level, accent, soft, headline, body) { Width = Width_ - PadX * 2 };
        banner.Location = new Point(PadX, y);
        Controls.Add(banner);
        return y + banner.Height + 10;
    }

    private static Severity LevelFor(ImportPlan plan)
    {
        if (plan.HasBlockers) return Severity.Blocker;
        if (plan.NeedsHumanChoice || plan.PreviouslyInstalledAt is not null) return Severity.Warning;
        return Severity.Info;
    }

    private static string HeadlineFor(ImportPlan plan) => plan.Relation switch
    {
        Relation.NoLocal => "Safe to install",
        Relation.FastForward => "Safe to install - this is the newer copy",
        Relation.Identical => "Nothing to do",
        Relation.Stale => "Careful: this copy is older than yours",
        Relation.Diverged => "Both PCs were played - you need to choose",
        Relation.Unregistered => "This PC already has a save with this name",
        Relation.Unrelated => "These are two different saves",
        _ => "Review this before continuing",
    };

    private static string Friendly(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return "another PC";
        var i = identity.IndexOf('\\');
        return i > 0 ? identity[..i] : identity;
    }
}

/// <summary>One side of the before/after comparison.</summary>
internal sealed class SidePanel : Panel
{
    public const int FixedHeight = 112;

    private readonly bool _highlight;

    public SidePanel(string heading, string line1, string line2, string line3, bool highlight)
    {
        _highlight = highlight;
        DoubleBuffered = true;
        BackColor = highlight ? Theme.GoodSoft : Theme.Surface;

        Controls.Add(Make(heading, Theme.SmallBold, Theme.Faint, 14));
        Controls.Add(Make(line1, Theme.Heading, highlight ? Theme.Good : Theme.Text, 36, 24));
        Controls.Add(Make(line2, Theme.Small, Theme.Muted, 63));
        Controls.Add(Make(line3, Theme.Small, Theme.Muted, 83));
    }

    private static Label Make(string text, Font font, Color color, int y, int h = 18) => new()
    {
        Text = text,
        Font = font,
        ForeColor = color,
        AutoSize = false,
        Bounds = new Rectangle(16, y, 320, h),
    };

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        foreach (Control c in Controls) c.Width = Math.Max(40, Width - 32);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Theme.Background);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.RoundRect(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), 6,
            BackColor, _highlight ? Theme.Good : Theme.Border);
    }
}

/// <summary>
/// A radio row drawn by hand. The stock RadioButton glyph is painted by the system in light
/// colours and cannot be restyled, which looks broken on a dark form.
/// </summary>
internal sealed class ChoiceRow : Panel
{
    public const int FixedHeight = 58;

    private readonly string _title;
    private readonly string _subtitle;
    private bool _checked;
    private bool _hover;

    public ChoiceRow(string title, string subtitle)
    {
        _title = title;
        _subtitle = subtitle;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        BackColor = Theme.Background;
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
    }

    public event EventHandler? Chosen;

    public bool Checked
    {
        get => _checked;
        set { _checked = value; Invalidate(); }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        if (_checked) return;
        Checked = true;
        Chosen?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        var fill = _checked ? Theme.AccentSoft : _hover ? Theme.SurfaceRaised : Theme.Surface;
        var border = _checked ? Theme.Accent : Focused ? Theme.BorderStrong : Theme.Border;
        Theme.RoundRect(g, r, 6, fill, border);

        var circle = new Rectangle(16, Height / 2 - 9, 18, 18);
        using (var p = new Pen(_checked ? Theme.Accent : Theme.BorderStrong, 2))
            g.DrawEllipse(p, circle);
        if (_checked)
        {
            using var dot = new SolidBrush(Theme.Accent);
            g.FillEllipse(dot, Rectangle.Inflate(circle, -5, -5));
        }

        TextRenderer.DrawText(g, _title, Theme.BodyBold,
            new Rectangle(46, 10, Width - 60, 20), Theme.Text, TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(g, _subtitle, Theme.Small,
            new Rectangle(46, 31, Width - 60, 18), Theme.Muted, TextFormatFlags.EndEllipsis);
    }
}
