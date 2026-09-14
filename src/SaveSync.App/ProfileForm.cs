using SaveSync.Core;

namespace SaveSync.App;

/// <summary>
/// The first screen: who is using this.
///
/// The list lives on the stick, so it follows the stick to whichever PC it is plugged into and a
/// laptop shared by two people needs no separate Windows accounts. Picking a name is also what
/// lets the rest of the app talk about people instead of machine names.
/// </summary>
public sealed class ProfileForm : Form
{
    private const int PadX = 28;
    private const int RowH = 56;

    private readonly ProfileStore _store;
    private readonly string? _preferredId;

    private readonly Panel _header = new();
    private readonly Label _question = new();
    private readonly Panel _body = new();
    private readonly LinkLabel _removeLink = new();

    private bool _creating;
    private bool _removing;
    private TextBox? _pendingFocus;
    private bool _showRemoveLink;

    public ProfileForm(ProfileStore store, string? preferredId)
    {
        _store = store;
        _preferredId = preferredId;

        Text = "7 Days to Die - Save Transfer";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 420);

        BuildChrome();

        // With nobody on the list there is nothing to pick, so go straight to making one.
        _creating = _store.Profiles.Count == 0;
        Render();

        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);
    }

    /// <summary>The person who was picked, or null if the window was closed.</summary>
    public Profile? Selected { get; private set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FocusPending();
    }

    /// <summary>
    /// Focus has to work both when the box is built before the window is shown and when it is
    /// rebuilt afterwards, so it is deferred rather than hung off a one-shot Shown handler.
    /// </summary>
    private void FocusPending()
    {
        if (_pendingFocus is null || _pendingFocus.IsDisposed) return;
        if (!Visible || !IsHandleCreated) return;
        var box = _pendingFocus;
        BeginInvoke(new Action(() => { if (!box.IsDisposed) { box.Focus(); box.SelectAll(); } }));
    }

    private void BuildChrome()
    {
        int w = ClientSize.Width - PadX * 2;

        _header.SetBounds(0, 0, ClientSize.Width, 86);
        _header.BackColor = Theme.Surface;
        _header.Paint += (_, e) =>
        {
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(accent, 0, _header.Height - 3, _header.Width, 3);
        };

        var brand = new Label
        {
            Font = Theme.Display,
            ForeColor = Theme.Text,
            AutoSize = false,
            Text = "7 Days to Die",
        };
        brand.SetBounds(PadX, 16, w, 30);

        var sub = new Label
        {
            Font = Theme.Heading,
            ForeColor = Theme.Accent,
            AutoSize = false,
            Text = "SAVE TRANSFER",
        };
        sub.SetBounds(PadX, 46, w, 22);

        _header.Controls.AddRange(new Control[] { brand, sub });
        Controls.Add(_header);

        _question.Font = Theme.Title;
        _question.ForeColor = Theme.Text;
        _question.AutoSize = false;
        _question.SetBounds(PadX, 106, w, 28);
        Controls.Add(_question);

        _body.SetBounds(PadX, 146, w, 200);
        _body.BackColor = Theme.Background;
        Controls.Add(_body);

        _removeLink.Font = Theme.Small;
        _removeLink.AutoSize = false;
        _removeLink.Size = new Size(200, 20);
        _removeLink.BackColor = Theme.Background;
        _removeLink.LinkColor = Theme.Faint;
        _removeLink.ActiveLinkColor = Theme.Accent;
        _removeLink.VisitedLinkColor = Theme.Faint;
        _removeLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _removeLink.Click += (_, _) => { _removing = !_removing; Render(); };
        Controls.Add(_removeLink);
    }

    private void Render()
    {
        _body.SuspendLayout();
        _pendingFocus = null;
        foreach (Control c in _body.Controls.Cast<Control>().ToList()) c.Dispose();
        _body.Controls.Clear();

        int w = _body.Width;
        int y = 0;

        if (_creating)
        {
            _question.Text = _store.Profiles.Count == 0 ? "What is your name?" : "Add someone new";

            var hint = new Label
            {
                Font = Theme.Small,
                ForeColor = Theme.Muted,
                AutoSize = false,
                Text = "Just a first name is fine. It is only used to label your saves.",
            };
            hint.SetBounds(0, y, w, 34);
            _body.Controls.Add(hint);
            y += 40;

            var box = new TextBox
            {
                Font = new Font(Theme.Body.FontFamily, 13f),
                BackColor = Theme.SurfaceRaised,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                MaxLength = 24,
            };
            box.SetBounds(0, y, w, 36);
            _body.Controls.Add(box);
            y += 50;

            var create = new FlatButton("Create", primary: true) { Width = 150, Height = 42 };
            create.SetBounds(w - 150, y, 150, 42);
            create.Click += (_, _) => TryCreate(box.Text);
            _body.Controls.Add(create);

            if (_store.Profiles.Count > 0)
            {
                var back = new FlatButton("Back") { Width = 110, Height = 42 };
                back.SetBounds(w - 150 - 12 - 110, y, 110, 42);
                back.Click += (_, _) => { _creating = false; Render(); };
                _body.Controls.Add(back);
            }

            y += 52;

            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                TryCreate(box.Text);
            };

            _showRemoveLink = false;
            _removeLink.Visible = false;
            _pendingFocus = box;
            Finish(y);
            FocusPending();
            return;
        }

        _question.Text = _removing ? "Remove which name?" : "Who is using this?";

        var ordered = _store.Profiles
            .OrderByDescending(p => p.Id == _preferredId)
            .ThenByDescending(p => p.LastUsedAt)
            .ToList();

        foreach (var profile in ordered)
        {
            var button = new FlatButton(profile.Name, primary: !_removing && profile.Id == _preferredId)
            {
                Width = w,
                Height = 48,
            };
            button.Font = new Font(Theme.Body.FontFamily, 12f, FontStyle.Bold);
            button.SetBounds(0, y, w, 48);

            var captured = profile;
            if (_removing) button.Click += (_, _) => TryRemove(captured);
            else button.Click += (_, _) => Pick(captured);

            _body.Controls.Add(button);
            y += RowH;
        }

        if (!_removing)
        {
            var add = new FlatButton("+  Add someone new") { Width = w, Height = 48 };
            add.SetBounds(0, y, w, 48);
            add.Click += (_, _) => { _creating = true; Render(); };
            _body.Controls.Add(add);
            y += RowH;
        }

        _showRemoveLink = _store.Profiles.Count > 0;
        _removeLink.Visible = _showRemoveLink;
        _removeLink.Text = _removing ? "Done removing" : "Remove a name";

        Finish(y);
    }

    private void Finish(int bodyHeight)
    {
        _body.Height = Math.Max(40, bodyHeight);
        _body.ResumeLayout();

        int bottom = _body.Top + _body.Height + 14;
        _removeLink.Location = new Point(PadX, bottom);

        // Deliberately not _removeLink.Visible: that getter reports EFFECTIVE visibility, which is
        // false while the form itself has not been shown yet, so during construction it always read
        // back false and the window was sized as though the link were not there.
        int wanted = bottom + (_showRemoveLink ? _removeLink.Height + 22 : 14);
        if (ClientSize.Height != wanted) ClientSize = new Size(ClientSize.Width, wanted);
    }

    private void TryCreate(string name)
    {
        var clean = ProfileStore.CleanName(name);
        if (clean.Length == 0)
        {
            Dialogs.Warn(this, "A name is needed", "Type a name first - just a first name is fine.");
            return;
        }

        var existing = _store.ByName(clean);
        if (existing is not null)
        {
            Pick(existing);
            return;
        }

        var profile = _store.Add(clean);
        Save();
        Pick(profile);
    }

    private void TryRemove(Profile profile)
    {
        if (!Dialogs.Confirm(this, "Remove this name",
                $"Remove \"{profile.Name}\" from the list?\n\n"
                + "This only removes the name. No saves and no backups are deleted.",
                "Remove it", dangerous: true))
            return;

        _store.Remove(profile);
        Save();

        if (_store.Profiles.Count == 0) { _removing = false; _creating = true; }
        Render();
    }

    private void Save()
    {
        try { _store.Save(); }
        catch (Exception ex)
        {
            Dialogs.Warn(this, "Could not save the list",
                "The name was not written to the stick, so it may not be there next time.\n\n" + ex.Message);
        }
    }

    private void Pick(Profile profile)
    {
        _store.MarkUsed(profile);
        Save();
        Selected = profile;
        DialogResult = DialogResult.OK;
        Close();
    }
}
