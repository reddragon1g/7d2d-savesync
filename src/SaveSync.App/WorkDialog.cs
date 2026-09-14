using SaveSync.Core;

namespace SaveSync.App;

/// <summary>
/// Runs a long job off the UI thread with progress and a Cancel that actually works.
///
/// Cancelling is safe at any point by construction: the engine only writes to staging until it has
/// verified every byte, so an abandoned job leaves the live save untouched.
/// </summary>
public sealed class WorkDialog : Form
{
    private readonly Label _headline = new();
    private readonly Label _detail = new();
    private readonly ThinProgress _bar = new();
    private readonly FlatButton _cancel = new("Cancel");
    private readonly CancellationTokenSource _cts = new();

    private WorkDialog(string title, string headline)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        ClientSize = new Size(540, 186);
        Font = Theme.Body;

        _headline.Font = Theme.Title;
        _headline.ForeColor = Theme.Text;
        _headline.AutoSize = false;
        _headline.Text = headline;
        _headline.SetBounds(26, 24, 488, 28);

        _detail.Font = Theme.Small;
        _detail.ForeColor = Theme.Muted;
        _detail.AutoSize = false;
        _detail.AutoEllipsis = true;
        _detail.SetBounds(26, 56, 488, 20);
        _detail.Text = "Starting...";

        _bar.SetBounds(26, 86, 488, 8);

        _cancel.SetBounds(406, 122, 108, 38);
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _detail.Text = "Stopping safely...";
            _cts.Cancel();
        };

        var note = new Label
        {
            Font = Theme.Small,
            ForeColor = Theme.Faint,
            AutoSize = false,
            Text = "Nothing is changed until every file has been checked.",
        };
        note.SetBounds(26, 132, 360, 18);

        Controls.AddRange(new Control[] { _headline, _detail, _bar, note, _cancel });
        HandleCreated += (_, _) => Theme.UseDarkTitleBar(this);
    }

    /// <summary>
    /// Runs <paramref name="work"/> and returns its result, or default when cancelled. Exceptions
    /// are rethrown on the caller's thread so callers handle failure normally.
    /// </summary>
    public static T? Run<T>(
        IWin32Window? owner,
        string title,
        string headline,
        Func<IProgress<ScanProgress>, CancellationToken, T> work)
    {
        using var dlg = new WorkDialog(title, headline);

        T? result = default;
        Exception? failure = null;
        bool cancelled = false;

        var progress = new Progress<ScanProgress>(p => dlg.Update(p));

        dlg.Shown += async (_, _) =>
        {
            try
            {
                result = await Task.Run(() => work(progress, dlg._cts.Token), dlg._cts.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { failure = ex; }
            finally { dlg.Close(); }
        };

        dlg.ShowDialog(owner);

        if (failure is not null) throw failure;
        return cancelled ? default : result;
    }

    public static bool Run(
        IWin32Window? owner,
        string title,
        string headline,
        Action<IProgress<ScanProgress>, CancellationToken> work)
    {
        var done = Run<object?>(owner, title, headline, (p, ct) => { work(p, ct); return new object(); });
        return done is not null;
    }

    private void Update(ScanProgress p)
    {
        if (IsDisposed || !IsHandleCreated) return;

        if (p.BytesTotal > 0)
        {
            _bar.Value = (double)p.BytesDone / p.BytesTotal;
            _detail.Text = $"{PathUtil.HumanBytes(p.BytesDone)} of {PathUtil.HumanBytes(p.BytesTotal)}"
                           + $"{Theme.Dot}{Trim(p.Current)}";
        }
        else if (p.FilesTotal > 0)
        {
            _bar.Value = (double)p.FilesDone / p.FilesTotal;
            _detail.Text = $"{p.FilesDone:N0} of {p.FilesTotal:N0} files{Theme.Dot}{Trim(p.Current)}";
        }
    }

    private static string Trim(string path)
        => path.Length <= 44 ? path : "..." + path[^41..];

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
