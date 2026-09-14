using SaveSync.Core;

namespace SaveSync.App;

/// <summary>
/// Every previous version of a save, with one-click restore.
///
/// This window is what lets the rest of the tool be confident: no action anywhere else destroys
/// anything, so there is always something here to go back to.
/// </summary>
public sealed class BackupsForm : Form
{
    private readonly TransferEngine _engine;
    private readonly SaveSlot _slot;
    private readonly ListView _list = new();
    private readonly FlatButton _restore = new("Put this one back", primary: true);
    private readonly FlatButton _delete = new("Delete");
    private readonly FlatButton _backupNow = new("Back this save up now");
    private readonly Label _empty = new();
    private readonly Label _hint = new();

    public BackupsForm(TransferEngine engine, SaveSlot slot)
    {
        _engine = engine;
        _slot = slot;

        Text = $"Backups - {slot.SaveName} ({slot.World})";
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ClientSize = new Size(900, 520);
        MinimumSize = new Size(760, 420);

        var heading = new Label
        {
            Font = Theme.Display,
            ForeColor = Theme.Text,
            AutoSize = false,
            Text = $"Earlier versions of {slot.SaveName}",
        };
        heading.SetBounds(24, 20, 700, 30);

        var sub = new Label
        {
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            AutoSize = false,
            Text = $"World: {slot.World}{Theme.Dot}{Shorten(slot.Folder, 92)}"
                   + Environment.NewLine
                   + "A backup is kept automatically every time this save is replaced, and kept for good - "
                   + "nothing here is ever removed unless you delete it yourself. "
                   + "Putting one back also backs up whatever is there now, so this is never a one-way door.",
        };
        sub.SetBounds(24, 52, 840, 52);

        BuildList();

        _empty.Font = Theme.Body;
        _empty.ForeColor = Theme.Muted;
        _empty.AutoSize = false;
        _empty.Text = "No backups yet. One is made automatically the first time this save is replaced - "
                      + "or press \"Back this save up now\" to make one straight away.";
        _empty.SetBounds(28, 126, 800, 24);
        _empty.Visible = false;

        _hint.Font = Theme.Small;
        _hint.ForeColor = Theme.Faint;
        _hint.AutoSize = false;
        _hint.SetBounds(24, ClientSize.Height - 58, 420, 34);
        _hint.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

        _restore.Width = 200;
        _restore.Height = 40;
        _restore.SetBounds(ClientSize.Width - 24 - 200, ClientSize.Height - 58, 200, 40);
        _restore.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _restore.Click += (_, _) => Restore();

        _delete.Width = 120;
        _delete.Height = 40;
        _delete.SetBounds(_restore.Left - 10 - 120, ClientSize.Height - 58, 120, 40);
        _delete.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _delete.Click += (_, _) => DeleteSelected();

        // Always available, never needs anything selected: this is the "just in case" button.
        _backupNow.Width = 190;
        _backupNow.Height = 40;
        _backupNow.SetBounds(_delete.Left - 10 - 190, ClientSize.Height - 58, 190, 40);
        _backupNow.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _backupNow.Click += (_, _) => BackupNow();

        _hint.Width = _backupNow.Left - 24 - 12;

        Controls.AddRange(new Control[] { heading, sub, _list, _empty, _hint, _restore, _delete, _backupNow });

        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);
        Reload();
    }

    /// <summary>Set when a restore happened, so the caller knows to refresh.</summary>
    public bool Changed { get; private set; }

    private void BuildList()
    {
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.Font = Theme.Body;
        _list.BackColor = Theme.Surface;
        _list.ForeColor = Theme.Text;
        _list.BorderStyle = BorderStyle.None;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.SetBounds(24, 112, ClientSize.Width - 48, ClientSize.Height - 112 - 74);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

        _list.Columns.Add("When", 190);
        _list.Columns.Add("Played on", 190);
        _list.Columns.Add("Version", 80);
        _list.Columns.Add("Size", 100);
        _list.Columns.Add("Why it was kept", 280);

        // The stock control paints headers and rows in system light colours, so it is drawn here.
        _list.OwnerDraw = true;
        _list.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(Theme.SurfaceRaised);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var line = new Pen(Theme.Border);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            var r = e.Bounds with { X = e.Bounds.X + 10, Width = e.Bounds.Width - 12 };
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Theme.SmallBold, r, Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };

        _list.DrawItem += (_, e) =>
        {
            bool selected = e.Item?.Selected ?? false;
            using var bg = new SolidBrush(selected ? Theme.AccentSoft : Theme.Surface);
            e.Graphics.FillRectangle(bg, e.Bounds);

            if (selected)
            {
                using var edge = new SolidBrush(Theme.Accent);
                e.Graphics.FillRectangle(edge, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
            }
        };

        _list.DrawSubItem += (_, e) =>
        {
            var snap = e.Item?.Tag as SnapshotInfo;
            var color = snap?.Pinned == true && e.ColumnIndex == 4 ? Theme.Warn
                : e.ColumnIndex == 0 ? Theme.Text
                : Theme.Muted;

            var r = e.Bounds with { X = e.Bounds.X + 10, Width = e.Bounds.Width - 12 };
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", Theme.Body, r, color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };

        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
    }

    private void Reload()
    {
        _list.Items.Clear();

        var saveId = _slot.Passport?.SaveId;
        var snapshots = saveId is null ? new List<SnapshotInfo>() : _engine.Snapshots.List(saveId);

        foreach (var s in snapshots)
        {
            var item = new ListViewItem(s.CreatedAt.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm")) { Tag = s };
            item.SubItems.Add(Friendly(s.Passport?.LastPlayedOn ?? ""));
            item.SubItems.Add(s.Passport is null ? "-" : s.Passport.Ordinal.ToString());
            item.SubItems.Add(PathUtil.HumanBytes(s.SizeBytes));
            item.SubItems.Add(s.Pinned ? s.Reason + "  (kept permanently)" : s.Reason);
            _list.Items.Add(item);
        }

        _empty.Visible = _list.Items.Count == 0;
        _list.Visible = _list.Items.Count > 0;
        UpdateButtons();
    }

    private SnapshotInfo? Selected
        => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as SnapshotInfo : null;

    private void UpdateButtons()
    {
        var sel = Selected;
        _restore.Enabled = sel is not null;
        _delete.Enabled = sel is not null && !sel.Pinned;

        _hint.Text = sel is null
            ? "Select a backup to see what it contains."
            : sel.Pinned
                ? "This backup is kept permanently because it is the only copy of that history."
                : $"{sel.FileCount:N0} files{Theme.Dot}{PathUtil.HumanBytes(sel.SizeBytes)}";
    }

    private void Restore()
    {
        var snap = Selected;
        if (snap is null) return;

        var blockers = TransferEngine.GlobalBlockers();
        if (blockers.Count > 0)
        {
            Dialogs.Warn(this, "Cannot do that right now", blockers[0].Message);
            return;
        }

        if (!Dialogs.Confirm(this, "Restore this backup",
                $"Put back {_slot.SaveName} ({_slot.World}) as it was on "
                + $"{snap.CreatedAt.ToLocalTime():ddd d MMM yyyy, HH:mm}?"
                + Environment.NewLine + Environment.NewLine
                + $"That version was last played by {Friendly(snap.Passport?.LastPlayedOn ?? "")} and holds "
                + $"{snap.FileCount:N0} files ({PathUtil.HumanBytes(snap.SizeBytes)})."
                + Environment.NewLine + Environment.NewLine
                + "The save currently on this PC is itself kept as a backup, so this can be undone.",
                "Put it back")) return;

        try
        {
            WorkDialog.Run(this, "Restoring", $"Putting back {_slot.SaveName} ({_slot.World})...",
                (_, _) => _engine.Snapshots.Restore(snap, _slot.Folder));

            Changed = true;
            Reload();
            Dialogs.Info(this, "Restored", "Done. That version is back in place.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Could not restore", ex.Message);
        }
    }

    /// <summary>
    /// Takes a copy of the save exactly as it is now, on demand.
    ///
    /// Pinned, because a backup somebody deliberately asked for is never the one to clean up: it
    /// was almost certainly made right before they tried something they were unsure about.
    /// </summary>
    private void BackupNow()
    {
        var blockers = TransferEngine.GlobalBlockers();
        if (blockers.Count > 0)
        {
            Dialogs.Warn(this, "Cannot do that right now", blockers[0].Message);
            return;
        }

        try
        {
            SnapshotInfo? made = null;
            WorkDialog.Run(this, "Backing up", $"Copying {_slot.SaveName} ({_slot.World})...", (_, _) =>
            {
                SaveDiscovery.Measure(_slot);
                made = _engine.Snapshots.CaptureCopy(_slot, "backup you asked for", pinned: true);
            });

            Changed = true;
            Reload();
            Dialogs.Info(this, "Backed up",
                made is null
                    ? "Done."
                    : $"Saved a copy of {_slot.SaveName} ({_slot.World}) as it is right now.\n\n"
                      + $"{made.FileCount:N0} files{Theme.Dot}{PathUtil.HumanBytes(made.SizeBytes)}\n\n"
                      + "It is at the top of the list, and it will not be removed on its own.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Could not back up", ex.Message);
        }
    }

    private void DeleteSelected()
    {
        var snap = Selected;
        if (snap is null || snap.Pinned) return;

        if (!Dialogs.Confirm(this, "Delete backup",
                $"Permanently delete the {_slot.SaveName} ({_slot.World}) backup from "
                + $"{snap.CreatedAt.ToLocalTime():ddd d MMM yyyy, HH:mm}?"
                + Environment.NewLine + Environment.NewLine
                + "This is the one thing here that cannot be undone.",
                "Delete it", dangerous: true)) return;

        try
        {
            _engine.Snapshots.Delete(snap);
            Reload();
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Could not delete", ex.Message);
        }
    }

    /// <summary>
    /// Keeps a long save path on one line by dropping the middle. The ends are what identify a
    /// save - the drive and the folder name - and the middle is the part nobody reads.
    /// </summary>
    private static string Shorten(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= max) return path;
        int keep = (max - 3) / 2;
        return path[..keep] + "..." + path[^keep..];
    }

    private static string Friendly(string identity)
        => string.IsNullOrWhiteSpace(identity) ? "unknown" : identity.Replace('\\', ' ');
}
