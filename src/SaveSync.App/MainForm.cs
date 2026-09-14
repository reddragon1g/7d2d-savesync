using System.Diagnostics;
using SaveSync.Core;
using SaveSync.Core.Lan;

namespace SaveSync.App;

/// <summary>
/// The whole app, on one screen.
///
/// Two buttons that never move: send my saves, or bring the newer ones here. Whether that happens
/// over the network or via the USB stick is the tool's problem, not the user's - it picks whichever
/// is available, says so in one line, and recommends the one that is actually needed. A question
/// is only ever asked when there is genuinely no safe answer.
/// </summary>
public sealed class MainForm : Form
{
    private const int PadX = 28;

    private readonly AppConfig _config;
    private readonly Profile _profile;

    private GameLocation? _location;
    private TransferEngine? _engine;
    private string? _stickRoot;
    private StickPlan? _plan;
    private bool _busy;
    private string _driveSignature = "";

    private LanServer? _server;
    private Discovery? _discovery;
    private PeerWatcher? _watcher;
    private IReadOnlyList<PeerNews> _news = Array.Empty<PeerNews>();

    private readonly Panel _header = new();
    private readonly Panel _info = new();
    private readonly Label _stickLine = new();
    private readonly Label _netLine = new();
    private readonly Label _youLine = new();

    private readonly FlowLayoutPanel _notices = new();
    private readonly Label _summary = new();

    private readonly BigButton _send = new("Send my saves");
    private readonly BigButton _receive = new("Bring the newer saves here");

    private readonly Label _reassurance = new();
    private readonly List<LinkLabel> _links = new();
    private readonly FlatButton _refresh = new("Refresh");

    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _watchTimer = new();
    private readonly AutoSyncState _autoState = new();
    private AutoSyncTrigger _lastTrigger = AutoSyncTrigger.None;

    /// <summary>The last thing written about the stick, so a still screen does not repeat itself.</summary>
    private string _lastAssessment = "";
    private readonly System.Windows.Forms.Timer _housekeeping = new();
    private bool _reallyClosing;

    private EventWaitHandle? _showRequest;
    private Thread? _showListener;
    private volatile bool _stopShowListener;

    /// <summary>
    /// Name of the signal a second copy uses to ask the running one to come to the front.
    ///
    /// Without it, the likeliest thing a user does - the installed copy is already sitting in the
    /// tray, they plug the stick in and double-click it - ends at an "already running" box with no
    /// window anywhere, which reads as broken.
    /// </summary>
    public const string ShowRequestEventName = "SaveSync.7DaysToDie.ShowWindow";

    public MainForm(AppConfig config, Profile profile, string? initialStick = null, bool startHidden = false)
    {
        _config = config;
        _profile = profile;
        _stickRoot = initialStick;
        StartHidden = startHidden;

        Text = "7 Days to Die - Save Transfer";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Icon = Theme.AppIcon;
        ClientSize = new Size(680, 620);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;

        Build();
        BuildTray();

        _watchTimer.Interval = 2000;
        _watchTimer.Tick += (_, _) => WatchDrives();

        // An installed copy can run for weeks without being restarted, so the tidy-up cannot only
        // happen at startup.
        _housekeeping.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
        _housekeeping.Tick += (_, _) =>
        {
            if (_engine is not null) Inbox.Cleanup(_engine.Workspace, TimeSpan.FromDays(14));
        };
        _housekeeping.Start();

    }

    private bool _started;

    /// <summary>
    /// Startup hangs off the handle, not off Load.
    ///
    /// Load only fires when a form is actually shown, and a copy started with Windows is
    /// deliberately never shown - so hooking Load there left the program running but completely
    /// inert: no engine, no network, nothing watching. It looked fine in Task Manager and did
    /// nothing at all.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(this);

        if (_started) return;
        _started = true;
        BeginInvoke(Startup);
    }

    /// <summary>Started with Windows: sit in the tray rather than jumping in front of the user.</summary>
    public bool StartHidden { get; }

    private bool _firstShowSuppressed;

    /// <summary>
    /// Suppresses only the very first show. Hiding inside Load does not work - WinForms makes the
    /// form visible again straight afterwards - so the window would flash up at every login.
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (StartHidden && !_firstShowSuppressed && value)
        {
            _firstShowSuppressed = true;
            CreateHandle();
            base.SetVisibleCore(false);
            return;
        }

        base.SetVisibleCore(value);
    }

    // ------------------------------------------------------------------ layout

    private void Build()
    {
        int w = ClientSize.Width - PadX * 2;

        _header.SetBounds(0, 0, ClientSize.Width, 92);
        _header.BackColor = Theme.Surface;
        _header.Paint += (_, e) =>
        {
            using var accent = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(accent, 0, _header.Height - 3, _header.Width, 3);
        };

        var brand = new Label
        {
            Font = Theme.Display, ForeColor = Theme.Text, AutoSize = false, Text = "7 Days to Die",
        };
        brand.SetBounds(PadX, 18, w, 30);

        var sub = new Label
        {
            Font = Theme.Heading, ForeColor = Theme.Accent, AutoSize = false, Text = "SAVE TRANSFER",
        };
        sub.SetBounds(PadX, 50, w, 22);

        _header.Controls.AddRange(new Control[] { brand, sub });
        Controls.Add(_header);

        _info.SetBounds(PadX, 112, w, 98);
        _info.BackColor = Theme.Surface;
        _info.Paint += (_, e) => Theme.RoundRect(e.Graphics,
            new Rectangle(0, 0, _info.Width - 1, _info.Height - 1), 6, Theme.Surface, Theme.Border);

        ConfigureInfoLine(_netLine, Theme.Text, 14, w);
        ConfigureInfoLine(_stickLine, Theme.Muted, 40, w);
        ConfigureInfoLine(_youLine, Theme.Muted, 64, w);
        _info.Controls.AddRange(new Control[] { _netLine, _stickLine, _youLine });
        Controls.Add(_info);

        _summary.Font = Theme.BodyBold;
        _summary.ForeColor = Theme.Text;
        _summary.AutoSize = false;
        _summary.SetBounds(PadX, 222, w, 22);
        Controls.Add(_summary);

        _notices.SetBounds(PadX, 248, w, 0);
        _notices.FlowDirection = FlowDirection.TopDown;
        _notices.WrapContents = false;
        _notices.AutoSize = true;
        _notices.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _notices.BackColor = Theme.Background;
        Controls.Add(_notices);

        _send.Click += (_, _) => DoSend();
        _receive.Click += (_, _) => DoReceive();
        Controls.Add(_send);
        Controls.Add(_receive);

        _reassurance.Font = Theme.Small;
        _reassurance.ForeColor = Theme.Faint;
        _reassurance.AutoSize = false;
        _reassurance.Text = "Nothing is ever deleted. Any save that gets replaced is kept as a backup you can put back.";
        Controls.Add(_reassurance);

        _links.Add(MakeLink("Not you?", SwitchProfile));
        _links.Add(MakeLink("Backups", ShowBackups));
        _links.Add(MakeLink("What it has been doing", ShowActivityLog));
        _links.Add(MakeLink("Check the other PC", AskThePeer));
        _links.Add(MakeLink("Use a different drive", ChooseStick));
        _links.Add(MakeLink("Find my saves", ChooseSavesFolder));
        foreach (var l in _links) Controls.Add(l);

        _refresh.Width = 110;
        _refresh.Height = 34;
        _refresh.Click += (_, _) => { RefreshNetwork(); Rebuild(); };
        Controls.Add(_refresh);

        Relayout();
    }

    private void ConfigureInfoLine(Label label, Color color, int y, int w)
    {
        label.Font = Theme.Body;
        label.ForeColor = color;
        label.AutoSize = false;
        label.AutoEllipsis = true;
        label.SetBounds(18, y, w - 36, 20);
    }

    /// <summary>
    /// Lays everything below the summary from the top down, so the window has no hole in it when
    /// there is nothing to warn about and nothing is clipped when there is a lot.
    /// </summary>
    private void Relayout()
    {
        int w = ClientSize.Width - PadX * 2;
        int y = 248;

        if (_notices.Controls.Count > 0)
        {
            _notices.SetBounds(PadX, y, w, _notices.PreferredSize.Height);
            y += _notices.Height + 14;
        }
        else
        {
            _notices.SetBounds(PadX, y, w, 0);
        }

        _send.SetBounds(PadX, y, w, 84);
        y += 94;

        _receive.SetBounds(PadX, y, w, 84);
        y += 84 + 18;

        _reassurance.SetBounds(PadX, y, w, 32);
        y += 38;

        int x = PadX;
        foreach (var link in _links)
        {
            link.Location = new Point(x, y + 8);
            x += link.Width + 22;
        }

        _refresh.Location = new Point(ClientSize.Width - PadX - _refresh.Width, y);
        y += _refresh.Height + 20;

        if (ClientSize.Height != y) ClientSize = new Size(ClientSize.Width, y);
    }

    private static LinkLabel MakeLink(string text, Action onClick)
    {
        var link = new LinkLabel
        {
            Font = Theme.Small,
            Text = text,
            AutoSize = true,
            BackColor = Theme.Background,
            LinkColor = Theme.Muted,
            ActiveLinkColor = Theme.Accent,
            VisitedLinkColor = Theme.Muted,
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        link.Click += (_, _) => onClick();
        return link;
    }

    private void BuildTray()
    {
        _tray.Icon = Theme.AppIcon;
        _tray.Text = "7 Days to Die - Save Transfer";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowWindow();

        var menu = new ContextMenuStrip { BackColor = Theme.Surface, ForeColor = Theme.Text, ShowImageMargin = false };
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { _reallyClosing = true; Close(); });
        _tray.ContextMenuStrip = menu;
    }

    /// <summary>Listens for another copy asking this one to show itself.</summary>
    private void StartShowListener()
    {
        try
        {
            _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return;
        }

        _showListener = new Thread(() =>
        {
            while (!_stopShowListener)
            {
                try
                {
                    if (!_showRequest.WaitOne(500)) continue;
                    if (_stopShowListener) return;
                    if (IsHandleCreated) BeginInvoke(ShowWindow);
                }
                catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "SaveSync show-request listener",
        };

        _showListener.Start();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ------------------------------------------------------------------ lifecycle

    private void Startup()
    {
        Installer.CleanupAfterUpdate();
        StartShowListener();

        _location = _config.Locate();
        _youLine.Text = "You: " + _profile.Name;

        if (_location is null)
        {
            _netLine.Text = "Cannot find your 7 Days to Die saves on this PC.";
            _stickLine.Text = "";
            Notice(Severity.Blocker, "Cannot find your saves",
                "7 Days to Die does not appear to have been run on this PC, or its save folder was "
                + "moved. Use \"Find my saves\" below to point the tool at it.");
            _send.SetState(false, "Cannot find your saves on this PC", false);
            _receive.SetState(false, "Cannot find your saves on this PC", false);
            return;
        }

        try
        {
            _engine = new TransferEngine(_config, _location) { Identity = _profile.Name };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A save folder on read-only media, or one Windows will not let this account write to.
            _netLine.Text = "Cannot write to the save folder on this PC.";
            Notice(Severity.Blocker, "Cannot use the save folder",
                _location.UserDataRoot + "  -  " + ex.Message
                + "  This is usually a folder on read-only media, or one this Windows account is "
                + "not allowed to change. Use \"Find my saves\" to point at the right one.");
            _send.SetState(false, "Cannot write to the save folder", false);
            _receive.SetState(false, "Cannot write to the save folder", false);
            return;
        }

        ActivityLog.Open(_engine.Workspace, Machine.Name);
        ActivityLog.Session($"started  v{TransferEngine.ToolVersion}  as {_profile.Name}  "
            + $"from {AppContext.BaseDirectory}  installed={Installer.IsInstalled}");
        ActivityLog.Write($"saves at  {_engine.Location.UserDataRoot}  ({_engine.Location.Provenance})");

        try
        {
            foreach (var note in _engine.RecoverInterrupted())
                Notice(Severity.Warning, "Recovered from an interrupted transfer", note);
        }
        catch (Exception ex)
        {
            Notice(Severity.Warning, "Could not finish tidying up a previous transfer", ex.Message);
        }

        Inbox.Cleanup(_engine.Workspace, TimeSpan.FromDays(14));

        _stickRoot ??= StickLocator.FindStick();
        if (_stickRoot is not null) ActivityLog.MirrorTo(_stickRoot);
        StartNetwork();
        Rebuild();
        _watchTimer.Start();
    }

    // ------------------------------------------------------------------ network

    private void StartNetwork()
    {
        if (_engine is null || !_config.UseNetwork) return;
        if (!FirewallSetup.IsConfigured()) return; // offered on screen instead

        _server = new LanServer(_config, () => _engine);
        _server.PackageArrived += OnPackageArrived;
        _server.UpdateStaged += OnUpdateStaged;
        _server.Start();

        _discovery = new Discovery(_config)
        {
            PersonName = _profile.Name,
            ServerPort = _server.Port,
        };
        _discovery.Start();

        _watcher = new PeerWatcher(_config, _discovery, () => _engine)
        {
            PersonName = _profile.Name,
            ReplyPort = _server.Port,
        };
        _watcher.NewsChanged += OnNewsChanged;

        // The watcher pings every tick and only does the expensive comparison when this says so.
        _watcher.ShouldCompare = () =>
        {
            var trigger = AutoSyncPolicy.Decide(
                enabled: true,
                gameRunning: GamePaths.IsGameRunning(),
                peerPresent: true,                      // only asked when somebody answered
                _autoState,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(Math.Max(5, _config.AutoSyncEveryMinutes)));

            if (trigger == AutoSyncTrigger.None) return false;

            _autoState.LastFullCheck = DateTimeOffset.UtcNow;
            _lastTrigger = trigger;
            ActivityLog.Write($"checking the other PC  ({AutoSyncPolicy.Explain(trigger)})");
            return true;
        };

        _watcher.PresenceChecked += present =>
        {
            // Feeds the policy the two facts it needs, on the cheap tick. Nothing is read from
            // disk here beyond a process list.
            if (!present)
            {
                AutoSyncPolicy.Decide(true, GamePaths.IsGameRunning(), false,
                    _autoState, DateTimeOffset.UtcNow);
            }
        };

        _watcher.Start();
    }

    private void RefreshNetwork()
    {
        if (_watcher is null) { StartNetwork(); return; }
        _ = Task.Run(async () =>
        {
            try { await _watcher.PollAsync(); }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException) { }
        });
    }

    private void OnNewsChanged(IReadOnlyList<PeerNews> news)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _news = news;

            foreach (var n in news)
                ActivityLog.Write($"  {n.Peer.Label}: {n.Display}  ->  {n.Direction}  ({n.Summary})");

            if (_config.AutoSync) { RunAutoSync(news); return; }

            var worth = news.Where(n => n.WorthFetching).ToList();
            if (worth.Count > 0)
            {
                var first = worth[0];
                _tray.ShowBalloonTip(8000, "A newer save is on the other PC",
                    $"{first.Summary} Open Save Transfer to bring it over.", ToolTipIcon.Info);
            }

            Rebuild();
        });
    }

    /// <summary>
    /// Moves what can be moved without asking anybody.
    ///
    /// Only the unambiguous direction in each case, and the receiving machine still judges every
    /// incoming package with its own engine - so this can never make a decision that would
    /// otherwise have been put to a person. Anything that needs thinking about is left exactly
    /// where it is, and said out loud rather than quietly skipped.
    /// </summary>
    private void RunAutoSync(IReadOnlyList<PeerNews> news)
    {
        var watcher = _watcher;
        if (watcher is null || _busy) return;

        var pull = news.Where(n => n.Direction == SyncDirection.ToPc).ToList();
        // Only saves that have been part of a transfer before. A save this PC has never sent
        // anywhere is a new introduction rather than an update, and introducing one machine's
        // private saves to another is not a decision to make on somebody's behalf - seen for real:
        // a laptop deciding to push its owner's own worlds onto a friend's PC because he happened
        // to have nothing by that name.
        var push = news
            .Where(n => n.Direction == SyncDirection.ToStick
                        && n.Local is not null
                        && n.Local.Passport is not null)
            .ToList();

        var unintroduced = news.Count(n => n.Direction == SyncDirection.ToStick && n.Local?.Passport is null);
        if (unintroduced > 0)
            ActivityLog.Write($"{unintroduced} save(s) here have never been copied anywhere; "
                + "leaving them for a person to send the first time");
        var stuck = news.Where(n => n.Direction == SyncDirection.Conflict).ToList();

        if (pull.Count == 0 && push.Count == 0)
        {
            _autoState.LastResult = stuck.Count > 0
                ? $"{stuck.Count} save{(stuck.Count == 1 ? "" : "s")} need you to choose"
                : "both PCs match";
            Rebuild();
            return;
        }

        _ = Task.Run(async () =>
        {
            var moved = new List<string>();

            foreach (var item in pull)
            {
                try { if (await watcher.FetchAsync(item)) moved.Add(item.Display); }
                catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException) { }
            }

            foreach (var item in push)
            {
                try { if (await PushAsync(item)) moved.Add(item.Display); }
                catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException
                                          or TransferBlockedException) { }
            }

            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                ActivityLog.Write(moved.Count > 0
                    ? "automatic transfer moved: " + string.Join(", ", moved)
                    : "automatic transfer moved nothing"
                      + (stuck.Count > 0 ? $" ({stuck.Count} waiting for a person)" : ""));

                // After writing it down, not before - mirroring first would carry home an account
                // that stops one line short of the thing it was mirrored to report.
                if (_stickRoot is not null) ActivityLog.MirrorTo(_stickRoot);

                if (moved.Count > 0)
                {
                    _autoState.LastTransfer = DateTimeOffset.UtcNow;
                    _autoState.LastResult = $"updated {string.Join(", ", moved)}";
                    _tray.ShowBalloonTip(6000, "Saves brought up to date",
                        string.Join(Environment.NewLine, moved.Select(m => "  " + m)), ToolTipIcon.Info);
                }
                else
                {
                    _autoState.LastResult = "nothing could be moved automatically";
                }

                if (stuck.Count > 0)
                {
                    _tray.ShowBalloonTip(8000, "Some saves need you",
                        $"{stuck.Count} save{(stuck.Count == 1 ? "" : "s")} were played on both PCs. "
                        + "Open Save Transfer to pick.", ToolTipIcon.Warning);
                }

                Rebuild();
            });
        });
    }

    /// <summary>Packages a save and sends it over. The far side decides whether to apply it.</summary>
    private async Task<bool> PushAsync(PeerNews item)
    {
        var engine = _engine;
        if (engine is null || item.Local is null) return false;

        var outbox = Path.Combine(engine.Workspace.Staging, "auto-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(outbox);
            var package = engine.Export(item.Local, outbox).PackageDir;
            var sent = await new LanClient(_config).SendPackageAsync(item.Peer, package, _profile.Name);
            return sent.Sent;
        }
        finally
        {
            try { PathUtil.DeleteTree(outbox); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A newer program has arrived from the other PC and has already been checked.
    ///
    /// Put in place immediately rather than offered, because this only ever happens on a machine
    /// whose owner switched remote updating on - having said yes once to the arrangement, being
    /// asked again every time is just the nuisance they were trying to avoid. It is announced, not
    /// hidden, and only the program changes.
    /// </summary>
    private void OnUpdateStaged(string stagedExe, string version)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            try
            {
                Installer.InstallFrom(stagedExe);
                ActivityLog.Write($"installed the update to {version} that arrived over the network");

                // Honest about when it takes effect: the copy running right now is still the old
                // one, because a program cannot replace itself underneath its own feet.
                _tray.ShowBalloonTip(8000, "This PC has a newer version ready",
                    $"Save Transfer {version} is installed and will be the one that runs from next "
                    + "time this PC starts. Your saves and backups are untouched.",
                    ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                ActivityLog.Write("could not install the update that arrived", ex);
                _tray.ShowBalloonTip(8000, "Update did not go on",
                    ex.Message + " The version already here still works.", ToolTipIcon.Warning);
            }
            finally
            {
                try { File.Delete(stagedExe); } catch (IOException) { }
            }

            Rebuild();
        });
    }

    private void OnPackageArrived(ReceivedPackage package)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _tray.ShowBalloonTip(8000,
                package.Applied ? "Save updated" : "A save arrived",
                package.Applied
                    ? $"{package.Item.Display} came over from {package.FromName} and is ready to play."
                    : $"{package.Item.Display} arrived from {package.FromName} but needs you to choose.",
                package.Applied ? ToolTipIcon.Info : ToolTipIcon.Warning);

            Rebuild();
        });
    }

    // ------------------------------------------------------------------ refresh

    private void WatchDrives()
    {
        if (_busy) return;

        var signature = string.Join("|", StickLocator.Candidates().Select(c => c.Root + c.FreeBytes));
        if (signature == _driveSignature) return;
        _driveSignature = signature;

        if (_stickRoot is null || !Directory.Exists(_stickRoot))
            _stickRoot = StickLocator.FindStick();

        Rebuild();
    }

    private void Rebuild()
    {
        if (_engine is null || _busy) return;

        _busy = true;
        var engine = _engine;
        var stick = _stickRoot;

        Task.Run(() => stick is not null && Directory.Exists(stick) ? StickSync.Build(engine, stick) : null)
            .ContinueWith(t =>
            {
                _busy = false;

                try
                {
                    _plan = t.IsFaulted ? null : t.Result;
                    Present(t.IsFaulted ? t.Exception?.GetBaseException() : null);
                }
                catch (Exception ex)
                {
                    _summary.Text = "Could not show what needs doing.";
                    Notice(Severity.Blocker, "Could not show what needs doing", ex.GetBaseException().Message);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Present(Exception? failure)
    {
        ClearNotices();

        var peer = _news.Select(n => n.Peer).FirstOrDefault();
        var fetchable = _news.Where(n => n.WorthFetching).ToList();
        var peerConflicts = _news.Count(n => n.Direction == SyncDirection.Conflict);
        var pushable = _news.Where(n => n.Direction == SyncDirection.ToStick).ToList();

        // ---- where we are ----
        if (!_config.UseNetwork)
            _netLine.Text = "Network transfer is turned off.";
        else if (!FirewallSetup.IsConfigured())
            _netLine.Text = "Network transfer: not switched on yet.";
        else if (peer is not null)
            _netLine.Text = $"Other PC: {peer.Label}{Theme.Dot}connected"
                            + (_config.AutoSync ? Theme.Dot + "keeping both up to date by itself" : "")
                            + AutoSyncStatus();
        else
            _netLine.Text = _server?.Running == true
                ? "Other PC: not switched on right now."
                : $"Network transfer could not start{(_server?.StartFailure is null ? "" : ": " + _server.StartFailure)}";

        var candidate = _plan is null
            ? null
            : StickLocator.Candidates().FirstOrDefault(c => PathUtil.SamePath(c.Root, _plan.StickRoot));
        _stickLine.Text = _plan is null
            ? "USB stick: none plugged in."
            : $"USB stick: {candidate?.Label ?? _plan.StickRoot}"
              + (candidate?.FreeBytes >= 0 ? $"{Theme.Dot}{PathUtil.HumanBytes(candidate.FreeBytes)} free" : "");

        int saveCount = _plan?.Items.Count ?? SaveDiscovery.Enumerate(_engine!.Location, measure: false).Count;
        _youLine.Text = $"You: {_profile.Name}{Theme.Dot}{saveCount} save{Theme.Plural(saveCount)} on this PC"
                        + ModCountSuffix();

        // ---- things that stop everything ----
        if (failure is not null)
        {
            _summary.Text = "Could not read the saves.";
            Notice(Severity.Blocker, "Could not read the saves", failure.Message);
            _send.SetState(false, "Something went wrong", false);
            _receive.SetState(false, "Something went wrong", false);
            Relayout();
            return;
        }

        if (GamePaths.IsGameRunning())
        {
            _summary.Text = "";
            Notice(Severity.Blocker, "Close the game first",
                "7 Days to Die is running. Copying a save while the game has it open produces a broken world.");
            _send.SetState(false, "Close 7 Days to Die first", false);
            _receive.SetState(false, "Close 7 Days to Die first", false);
            Relayout();
            return;
        }

        // ---- the one-time Windows permission ----
        if (_config.UseNetwork && !FirewallSetup.IsConfigured())
        {
            _notices.Controls.Add(new Banner(Severity.Info,
                "Transfer straight to the other PC",
                "Windows has to allow this once on this PC. It takes one click and is never asked again.",
                ("Allow over the network", AllowNetwork))
            {
                Width = ClientSize.Width - PadX * 2 - 6,
            });
        }

        // ---- the one permission that pairing never grants on its own ----
        if (_config.UseNetwork && FirewallSetup.IsConfigured() && Installer.IsInstalled && !_config.AllowRemoteUpdate)
        {
            _notices.Controls.Add(new Banner(Severity.Info,
                "Let the other PC update this program",
                "So nobody has to carry the USB stick over just to install a newer version. This is "
                + "the only thing the other PC cannot already do, because it means running a "
                + "program it sent - so it is off until you say otherwise.",
                ("Allow updates from the other PC", AllowRemoteUpdates))
            {
                Width = ClientSize.Width - PadX * 2 - 6,
            });
        }

        // ---- offer the hands-off mode, once it can actually work ----
        if (_config.UseNetwork && FirewallSetup.IsConfigured() && Installer.IsInstalled && !_config.AutoSync)
        {
            _notices.Controls.Add(new Banner(Severity.Info,
                "Keep both PCs up to date by themselves",
                "They already see each other. Switch this on and neither of you has to press "
                + "anything: whichever PC has the newer save sends it across on its own. It never "
                + "does it while the game is open, and anything that needs a decision still waits "
                + "for you.",
                ("Do it automatically", TurnOnAutoSync))
            {
                Width = ClientSize.Width - PadX * 2 - 6,
            });
        }

        // ---- offer to update an older copy already installed here ----
        //
        // This used to happen only when the older copy was already running and holding the
        // single-instance slot. If it happened not to be running, the newer copy started
        // normally from the stick and never said a word - so the PC quietly kept its old
        // version, and the person carrying the stick around had no way to know.
        if (Installer.IsInstalled && Installer.InstalledIsOlder && !Installer.RunningInstalled)
        {
            _notices.Controls.Add(new Banner(Severity.Info,
                $"This PC has an older version ({Installer.InstalledVersion})",
                $"The copy on this stick is newer ({Installer.ThisVersion}). Updating takes a "
                + "second and changes nothing about your saves, backups or settings.",
                ("Update this PC", UpdateInstalled))
            {
                Width = ClientSize.Width - PadX * 2 - 6,
            });
        }

        // ---- offer installing, when running from the stick ----
        if (!Installer.IsInstalled && !Installer.RunningInstalled)
        {
            _notices.Controls.Add(new Banner(Severity.Info,
                "Install on this PC",
                "Installing lets it run quietly in the background so this PC notices on its own when "
                + "the other one has a newer save.",
                ("Install on this PC", InstallHere))
            {
                Width = ClientSize.Width - PadX * 2 - 6,
            });
        }

        // ---- what the two buttons do right now ----
        int outward = _plan?.ToStick.Count() ?? 0;
        int inward = _plan?.ToPc.Count() ?? 0;

        // Written down because "I plugged it in and it never asked me anything" is otherwise a
        // dead end: there is no way afterwards to tell a stick that held nothing from a scan that
        // never happened from a scan that found things and offered them to somebody who did not
        // press the button.
        var assessment = _plan is null
            ? $"no stick found (looked at {_stickRoot ?? "nothing"})"
            : $"stick {_plan.StickRoot}: {_plan.Items.Count} save(s) seen, "
              + $"{outward} to send, {inward} to bring here, {_plan.Conflicts.Count()} needing a person";

        if (assessment != _lastAssessment)
        {
            _lastAssessment = assessment;
            ActivityLog.Write(assessment);
            foreach (var item in _plan?.Items ?? Enumerable.Empty<SyncItem>())
                ActivityLog.Write($"  {item.Display}  ->  {item.Direction}  ({item.Reason})");
        }
        int stickConflicts = _plan?.Conflicts.Count() ?? 0;
        int conflicts = stickConflicts + peerConflicts;

        bool canSendNet = peer is not null && pushable.Count > 0;
        bool canSendStick = outward > 0;
        bool canGetNet = fetchable.Count > 0;
        bool canGetStick = inward > 0;

        _send.Title = canSendNet && !canSendStick
            ? $"Send my saves to {peer!.Label}"
            : "Copy my saves onto the USB stick";
        _send.SetState(canSendNet || canSendStick,
            canSendStick ? $"{Describe(_plan!.ToStick)}{Theme.Dot}{PathUtil.HumanBytes(_plan.ToStickBytes)}"
            : canSendNet ? $"{pushable.Count} save{Theme.Plural(pushable.Count)} the other PC does not have yet"
            : _plan is null && peer is null ? "Plug in the USB stick, or switch on network transfer"
            : "Nothing here is newer than the other side",
            (canSendNet || canSendStick) && !canGetNet && !canGetStick && conflicts == 0);

        int stickChoices = (_plan?.ToPc.Count() ?? 0) + (_plan?.Conflicts.Count() ?? 0);
        _receive.Title = canGetNet && !canGetStick
            ? $"Bring the newer saves from {peer!.Label}"
            : canGetStick && stickChoices > 1
                ? "Choose which saves to put on this PC"
                : "Put the saves from the USB stick onto this PC";
        _receive.SetState(canGetNet || canGetStick,
            canGetNet ? fetchable[0].Summary
            : canGetStick ? $"{Describe(_plan!.ToPc)}{Theme.Dot}{PathUtil.HumanBytes(_plan.ToPcBytes)}"
            : "Nothing newer anywhere else",
            (canGetNet || canGetStick) && conflicts == 0);

        // ---- what is waiting for a person ----
        var waiting = _engine is null ? new List<InboxItem>() : Inbox.List(_engine.Workspace);
        if (waiting.Count > 0 || conflicts > 0)
        {
            int total = Math.Max(conflicts, waiting.Count);

            // Name them, and give the reason that actually applies. This said "both sides were
            // played since they last matched" whatever the cause - which is one specific case, and
            // simply untrue of the commonest one, a save that merely shares a name. Somebody read
            // that, could not match it to anything they had done, and had no idea what was wanted.
            var names = (_plan?.Conflicts.Select(c => c.Display) ?? Enumerable.Empty<string>())
                .Concat(waiting.Select(w => w.Display))
                .Distinct()
                .ToList();

            var which = names.Count == 0 ? ""
                : names.Count <= 3 ? string.Join(", ", names)
                : $"{names[0]}, {names[1]} and {names.Count - 2} more";

            _notices.Controls.Add(new Banner(Severity.Warning,
                total == 1
                    ? $"Waiting for you: {(which.Length > 0 ? which : "one save")}"
                    : $"Waiting for you: {total} saves",
                (which.Length > 0 && total > 1 ? which + ". " : "")
                + "This PC already has a save with the same name, so nothing has been touched. "
                + "The usual answer is to keep both - yours stays exactly as it is and the incoming "
                + "one is added next to it under a different name. Nothing is replaced unless you "
                + "say so.",
                ("Show me what to do", ReviewConflicts))
            {
                Width = ClientSize.Width - PadX * 2 - 6,
            });
        }

        _summary.Text = (canSendNet || canSendStick, canGetNet || canGetStick, conflicts) switch
        {
            (false, false, 0) => "Everything matches. There is nothing to do.",
            (true, false, 0) => "This PC has the newer saves.",
            (false, true, 0) => peer is not null && canGetNet
                ? $"{peer.Label} has newer saves."
                : "The USB stick has the newer saves.",
            (_, _, 0) => "Some saves need to go each way.",
            _ => "",
        };

        Relayout();
    }

    /// <summary>
    /// " - 4 mods" on the end of the identity line, when there are any.
    ///
    /// Counting only: no file is opened, so this stays cheap on every refresh. A mod folder that
    /// cannot be read costs the count, never the window - the rest of the screen is still correct
    /// and still usable without it.
    /// </summary>
    private string ModCountSuffix()
    {
        if (_engine is null || !_config.IncludeMods) return "";

        try
        {
            int mods = Mods.Enumerate(_engine.Location).Count;
            return mods == 0 ? "" : $"{Theme.Dot}{mods} mod{Theme.Plural(mods)} that will go with them";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>
    /// "last checked 20 minutes ago - both PCs match". Said out loud on purpose: the failure this
    /// guards against is arriving somewhere and finding an old save, and the only thing worse than
    /// that is the program having quietly believed it was fine.
    /// </summary>
    private string AutoSyncStatus()
    {
        if (_autoState.LastFullCheck is null) return "";

        var line = Theme.Dot + "last checked " + Theme.Ago(_autoState.LastFullCheck.Value);
        if (!string.IsNullOrWhiteSpace(_autoState.LastResult)) line += Theme.Dot + _autoState.LastResult;
        return line;
    }

    private static string Describe(IEnumerable<SyncItem> items)
    {
        var list = items.ToList();

        // Names, not a count. "3 saves" tells somebody nothing about whether the one they care
        // about is in there, which is the only question they are actually asking.
        return list.Count switch
        {
            0 => "",
            1 => list[0].Display,
            2 => $"{list[0].SaveName} and {list[1].SaveName}",
            3 => $"{list[0].SaveName}, {list[1].SaveName} and {list[2].SaveName}",
            _ => $"{list[0].SaveName}, {list[1].SaveName} and {list.Count - 2} more",
        };
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Replaces an older installed copy with the one running now.</summary>
    private void UpdateInstalled()
    {
        if (!Dialogs.Confirm(this, "Update this PC",
                $"Replace the version installed on this PC ({Installer.InstalledVersion}) with this "
                + $"one ({Installer.ThisVersion})?"
                + Environment.NewLine + Environment.NewLine
                + "Your saves, your backups and your settings are not touched - only the program "
                + "itself changes. If the old one is running in the background it will be closed "
                + "and the new one started in its place.",
                "Update it"))
            return;

        try
        {
            WorkDialog.Run(this, "Updating", "Replacing the installed copy...", (_, _) => Installer.Install());
            ActivityLog.Write($"installed copy updated to {Installer.ThisVersion} from {AppContext.BaseDirectory}");

            // Offer to hand over to it. Otherwise the copy left running is this one - off a USB
            // stick that is about to be unplugged - and the freshly installed copy sits idle until
            // the next login. Seen for real on two machines at once: both were running an orphaned
            // stick copy while their installed copy, the one that survives a reboot, was older.
            if (!Installer.RunningInstalled && Dialogs.Confirm(this, "Updated",
                    $"This PC is now on {Installer.ThisVersion}."
                    + Environment.NewLine + Environment.NewLine
                    + "Right now you are using the copy on the USB stick. Switch to the one just "
                    + "installed on this PC? That is the copy that starts with Windows and keeps "
                    + "working after you take the stick out."
                    + Environment.NewLine + Environment.NewLine
                    + "This window will close and the installed one will open.",
                    "Switch to the installed copy"))
            {
                ActivityLog.Write("handing over to the installed copy");
                if (Installer.LaunchInstalled())
                {
                    _reallyClosing = true;
                    Close();
                    return;
                }

                Dialogs.Warn(this, "Could not start it",
                    "The installed copy did not start. This one still works.");
            }
            else
            {
                Dialogs.Info(this, "Updated",
                    $"This PC is now on {Installer.ThisVersion}. Everything else is exactly as it was.");
            }
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Could not update this PC",
                ex.Message + Environment.NewLine + Environment.NewLine
                + "The older version is still installed and still works.");
        }

        Rebuild();
    }

    private void AllowRemoteUpdates()
    {
        if (!Dialogs.Confirm(this, "Allow updates from the other PC",
                "The other PC will be able to replace the Save Transfer program on this one with a "
                + "newer version, without anybody sitting here."
                + Environment.NewLine + Environment.NewLine
                + "Only a newer version is accepted, and only if it arrives intact - it is checked "
                + "against a checksum before anything is put in place. Your saves and backups are "
                + "never touched by it."
                + Environment.NewLine + Environment.NewLine
                + "Say no if you would rather carry the stick over.",
                "Allow it"))
            return;

        _config.AllowRemoteUpdate = true;
        try { _config.Save(); } catch (IOException) { }
        ActivityLog.Write("this PC now accepts program updates over the network");

        Dialogs.Info(this, "Allowed",
            "This PC will take newer versions from the other one. You can still update it by hand "
            + "from the stick any time.");

        Rebuild();
    }

    private void TurnOnAutoSync()
    {
        if (!Dialogs.Confirm(this, "Keep both PCs up to date automatically",
                "From now on, whichever PC has the newer save will send it to the other by itself."
                + Environment.NewLine + Environment.NewLine
                + "It only ever moves a save when there is one obvious answer - the same rule that "
                + "lights up the orange button. Anything that was played on both PCs still waits "
                + "for you to choose, and nothing happens at all while the game is open."
                + Environment.NewLine + Environment.NewLine
                + "Everything replaced is still kept as a backup you can put back.",
                "Yes, do it automatically"))
            return;

        _config.AutoSync = true;
        try { _config.Save(); } catch (IOException) { }

        Dialogs.Info(this, "Switched on",
            "You can leave it alone now. Do the same on the other PC and neither of you has to "
            + "think about it again.");

        RefreshNetwork();
        Rebuild();
    }

    private void AllowNetwork()
    {
        if (FirewallSetup.IsConfigured()) { StartNetwork(); Rebuild(); return; }

        if (!Dialogs.Confirm(this, "Allow transfers over the network",
                "Windows will ask for permission once. After that the two PCs can send saves to each "
                + "other by themselves, with nothing else to set up.\n\nGo ahead?",
                "Yes, allow it"))
            return;

        bool ok = FirewallSetup.RequestElevatedSetup(_config.LanPort, LanProtocol.DiscoveryPort);

        if (ok)
        {
            StartNetwork();
            Dialogs.Info(this, "Network transfer is on",
                "This PC can now send and receive saves over your network. Do the same on the other PC.");
        }
        else
        {
            Dialogs.Warn(this, "Not switched on",
                "Windows did not give permission, so transfers over the network are not available on "
                + "this PC yet. The USB stick still works exactly as before, and you can try again "
                + "any time with the same button.");
        }

        Rebuild();
    }

    private void InstallHere()
    {
        if (!Dialogs.Confirm(this, "Install on this PC",
                "This copies the program into your own user folder and starts it with Windows, so it "
                + "can quietly notice when the other PC has a newer save.\n\n"
                + "It does not need administrator rights and changes nothing about the game.",
                "Install it"))
            return;

        try
        {
            Installer.Install();
            Dialogs.Info(this, "Installed",
                "It will start with Windows from now on and sit quietly in the notification area "
                + "next to the clock.\n\nYou can keep using the copy on the USB stick as well.");
            Rebuild();
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Could not install", ex.Message);
        }
    }

    private void DoSend()
    {
        if (_engine is null || _busy) return;

        var peer = _news.Select(n => n.Peer).FirstOrDefault();
        var pushable = _news.Where(n => n.Direction == SyncDirection.ToStick).ToList();
        bool canSendStick = _plan is not null && _plan.ToStick.Any();

        if (!canSendStick && peer is not null && pushable.Count > 0)
        {
            SendOverNetwork(peer, pushable);
            return;
        }

        if (_plan is null) return;

        var engine = _engine;
        var plan = _plan;

        try
        {
            var outcome = WorkDialog.Run(this, "Copying to the USB stick",
                "Copying your saves and checking every file...",
                (p, ct) => StickSync.CopyToStick(engine, plan, p, ct));

            if (outcome is null) { _summary.Text = "Stopped. Nothing was changed."; Rebuild(); return; }

            Report("Copied to the USB stick", outcome, "You can unplug the stick and take it to the other PC.");
        }
        catch (TransferBlockedException ex) { Dialogs.Warn(this, "Could not copy", ex.Message); }
        catch (Exception ex) { Dialogs.Error(this, "Could not copy", ex.Message); }

        Rebuild();
    }

    private void SendOverNetwork(LanPeer peer, List<PeerNews> items)
    {
        if (_engine is null) return;
        var engine = _engine;
        var person = _profile.Name;

        try
        {
            var sent = WorkDialog.Run(this, $"Sending to {peer.Label}",
                "Checking every file and sending it across...",
                (progress, ct) =>
                {
                    var done = new List<string>();
                    var outbox = Path.Combine(engine.Workspace.Staging, "outgoing-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(outbox);

                    try
                    {
                        foreach (var item in items)
                        {
                            if (item.Local is null) continue;
                            var package = engine.Export(item.Local, outbox, true, progress, ct).PackageDir;
                            var result = new LanClient(_config)
                                .SendPackageAsync(peer, package, person, progress, ct)
                                .GetAwaiter().GetResult();

                            done.Add(result.Sent
                                ? $"{item.Display}: {(result.AppliedRemotely ? "installed on " + peer.Label : result.Message)}"
                                : $"{item.Display}: {result.Error}");
                        }
                    }
                    finally
                    {
                        try { PathUtil.DeleteTree(outbox); } catch (IOException) { }
                    }

                    return done;
                });

            if (sent is null) { _summary.Text = "Stopped. Nothing was changed."; Rebuild(); return; }

            Dialogs.Info(this, $"Sent to {peer.Label}", string.Join("\n\n", sent));
        }
        catch (TransferBlockedException ex) { Dialogs.Warn(this, "Could not send", ex.Message); }
        catch (Exception ex) { Dialogs.Error(this, "Could not send", ex.Message); }

        RefreshNetwork();
        Rebuild();
    }

    private void DoReceive()
    {
        if (_engine is null || _busy) return;

        // Everything the other PC is offering, whether or not it can be taken automatically, so a
        // save that needs a decision is at least visible rather than silently missing from the list.
        var offeredOverNetwork = _news
            .Where(n => n.WorthFetching || n.Direction == SyncDirection.Conflict)
            .ToList();

        if (offeredOverNetwork.Count > 0 && _watcher is not null)
        {
            var wanted = offeredOverNetwork.Where(n => n.WorthFetching).ToList();

            if (offeredOverNetwork.Count > 1)
            {
                using var picker = new PickSavesDialog(offeredOverNetwork
                    .Select(n => new PickItem(
                        n.Remote.SaveName, n.Remote.World, n.Summary, n.Remote.SizeBytes, n.WorthFetching, n))
                    .ToList());

                if (picker.ShowDialog(this) != DialogResult.OK) return;
                wanted = picker.ChosenAs<PeerNews>();
            }

            if (wanted.Count == 0) return;
            FetchOverNetwork(wanted);
            return;
        }

        if (_plan is null) return;

        var engine = _engine;
        var plan = _plan;

        // More than one save on the stick means there is a real choice to make, and doing them all
        // as one lump means a single save that needs a decision holds up the ones that do not.
        var offered = plan.ToPc.Concat(plan.Conflicts).ToList();
        List<SyncItem>? chosen = null;
        if (offered.Count > 1)
        {
            using var picker = new PickSavesDialog(offered
                .Select(i => new PickItem(
                    i.SaveName, i.World, i.Reason, i.Bytes, i.Direction == SyncDirection.ToPc, i))
                .ToList());

            if (picker.ShowDialog(this) != DialogResult.OK) return;

            chosen = picker.ChosenAs<SyncItem>();
            if (chosen.Count == 0) return;
        }

        try
        {
            var outcome = WorkDialog.Run(this, "Putting saves on this PC",
                "Checking every file, then installing...",
                (p, ct) => StickSync.CopyToPc(engine, plan, p, ct, chosen));

            if (outcome is null) { _summary.Text = "Stopped. Nothing was changed."; Rebuild(); return; }

            Report("Saves are now on this PC", outcome, "You can start the game.");
        }
        catch (TransferBlockedException ex)
        {
            Dialogs.Warn(this, "Could not do that", ex.Message + "\n\nNothing on this PC was changed.");
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Could not do that", ex.Message + "\n\nNothing on this PC was changed.");
        }

        Rebuild();
    }

    private void FetchOverNetwork(List<PeerNews> items)
    {
        var watcher = _watcher;
        if (watcher is null) return;

        var asked = WorkDialog.Run(this, "Asking the other PC",
            "Asking it to send the newer saves across...",
            (_, ct) =>
            {
                var names = new List<string>();
                foreach (var item in items)
                {
                    if (watcher.FetchAsync(item, ct).GetAwaiter().GetResult()) names.Add(item.Display);
                }
                return names;
            });

        if (asked is null) return;

        Dialogs.Info(this,
            asked.Count > 0 ? "On its way" : "Could not ask",
            asked.Count > 0
                ? string.Join("\n", asked.Select(n => "  " + n))
                  + "\n\nThe other PC is sending now. You will get a message here when it has arrived, "
                  + "and it will be checked file by file before anything is replaced."
                : "The other PC did not answer. It may have been closed or gone off the network.");

        Rebuild();
    }

    private void Report(string title, SyncOutcome outcome, string next)
    {
        ActivityLog.Write($"{title}: copied {outcome.Copied.Count}, skipped {outcome.Skipped.Count}, "
            + $"{outcome.NeedsChoice.Count} waiting for a person");

        // Onto the stick NOW, not at the next startup. Mirroring only on the way in meant the
        // stick went home carrying an account that stopped just before the part worth reading -
        // the transfer that had only just happened.
        if (_stickRoot is not null) ActivityLog.MirrorTo(_stickRoot);


        var lines = new List<string>
        {
            outcome.Copied.Count > 0
                ? "Done:\n    " + string.Join("\n    ", outcome.Copied)
                : "Nothing needed copying.",
        };

        foreach (var f in outcome.Findings.Where(f => f.Severity != Severity.Info))
            lines.Add(f.Message);

        if (outcome.NeedsChoice.Count > 0)
            lines.Add($"{outcome.NeedsChoice.Count} save(s) still need you to choose. Use \"Sort it out\".");

        lines.Add(next);
        Dialogs.Info(this, title, string.Join("\n\n", lines));
    }

    private void ReviewConflicts()
    {
        if (_engine is null) return;

        var packages = new List<string>();
        if (_plan is not null)
            packages.AddRange(_plan.Conflicts.Where(c => c.PackageDir is not null).Select(c => c.PackageDir!));
        packages.AddRange(Inbox.List(_engine.Workspace).Select(i => i.Dir));

        if (packages.Count == 0)
        {
            Dialogs.Info(this, "Nothing to sort out",
                "There is nothing waiting for a decision right now.");
            return;
        }

        foreach (var dir in packages.Distinct())
        {
            ImportPlan importPlan;
            try { importPlan = _engine.Inspect(dir); }
            catch (Exception ex) { Dialogs.Warn(this, "Could not read that save", ex.Message); continue; }

            using var dialog = new ImportDialog(importPlan);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                if (dialog.Choice == ImportChoice.KeepLocal && PathUtil.IsUnder(dir, _engine.Workspace.Inbox))
                    Inbox.Discard(dir);
                continue;
            }

            try
            {
                var engine = _engine;
                var choice = dialog.Choice;
                var result = WorkDialog.Run(this, "Installing",
                    $"Installing {importPlan.Info.Passport.SaveName}...",
                    (p, ct) => engine.Import(importPlan, choice, p, ct));

                if (result is null) continue;

                if (PathUtil.IsUnder(dir, _engine.Workspace.Inbox)) Inbox.Discard(dir);

                var lines = new List<string> { $"{importPlan.Info.Passport.SaveName} is now the copy on this PC." };
                lines.AddRange(result.Findings.Select(f => f.Message));
                Dialogs.Info(this, "Done", string.Join("\n\n", lines));
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "Could not install", ex.Message + "\n\nNothing on this PC was changed.");
            }
        }

        Rebuild();
    }

    /// <summary>
    /// Opens the account of what this copy has done.
    ///
    /// The question it answers is "did it ever even look?", which is the first thing worth knowing
    /// when somebody arrives somewhere and finds an old save - and the one thing that was
    /// impossible to find out before this existed.
    /// </summary>
    private void ShowActivityLog()
    {
        var path = ActivityLog.FilePath;
        if (path is null || !File.Exists(path))
        {
            Dialogs.Info(this, "Nothing recorded yet",
                "This copy has not done anything worth writing down since it started.");
            return;
        }

        // Copy it onto the stick on the way past, so the other PC's story can come home too.
        if (_stickRoot is not null) ActivityLog.MirrorTo(_stickRoot);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Dialogs.Warn(this, "Could not open it", ex.Message + Environment.NewLine + Environment.NewLine + path);
        }
    }

    /// <summary>
    /// Asks the other PC what it has been doing, and offers to hand it this version.
    ///
    /// The point of both: a machine somewhere else in the house can be looked at and brought up to
    /// date without walking over to it, or carrying a stick to it.
    /// </summary>
    private void AskThePeer()
    {
        var peer = _news.Select(n => n.Peer).FirstOrDefault();
        if (peer is null || _watcher is null)
        {
            Dialogs.Info(this, "No other PC right now",
                "Nothing has answered on this network. The other PC may be off, asleep, or not "
                + "have the program running.");
            return;
        }

        var fetched = WorkDialog.Run(this, "Asking the other PC", $"Asking {peer.Label}...",
            (_, ct) => new LanClient(_config).GetLogAsync(peer, _profile.Name, ct).GetAwaiter().GetResult());

        if (fetched is null)
        {
            Dialogs.Warn(this, "No answer", $"{peer.Label} did not answer.");
            return;
        }

        // Keep it, so it can be read properly rather than squinted at in a message box.
        string? saved = null;
        try
        {
            var dir = _engine is not null ? _engine.Workspace.Logs : Path.GetTempPath();
            Directory.CreateDirectory(dir);
            saved = Path.Combine(dir, PathUtil.Sanitize(fetched.Label) + ".log");
            File.WriteAllText(saved, fetched.Text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { saved = null; }

        var theirVersion = fetched.ToolVersion;
        bool canOffer = Version.TryParse(theirVersion, out var theirs)
                        && theirs < Installer.ThisVersion;

        var summary = $"{peer.Label} is running version "
                      + (string.IsNullOrWhiteSpace(theirVersion) ? "(unknown)" : theirVersion) + "."
                      + Environment.NewLine + Environment.NewLine
                      + (saved is not null
                          ? "Its log has been saved here:" + Environment.NewLine + saved
                          : "Its log could not be saved on this PC.");

        if (!canOffer)
        {
            Dialogs.Info(this, "The other PC", summary);
            return;
        }

        if (!Dialogs.Confirm(this, "The other PC",
                summary + Environment.NewLine + Environment.NewLine
                + $"This PC is on {Installer.ThisVersion}. Send it this version?"
                + Environment.NewLine + Environment.NewLine
                + "That PC has to have been set to accept updates, and it will say so if it has not.",
                "Send the update"))
            return;

        var exe = Environment.ProcessPath;
        if (exe is null) return;

        var sent = WorkDialog.Run(this, "Sending the program", $"Sending to {peer.Label}...",
            (_, ct) =>
            {
                var r = new LanClient(_config).PushUpdateAsync(peer, exe, _profile.Name, ct).GetAwaiter().GetResult();
                return new SendOutcome(r.Sent, r.Message);
            });

        if (sent is null) return;
        if (sent.Ok) Dialogs.Info(this, "Sent", sent.Message);
        else Dialogs.Warn(this, "Not sent", sent.Message);
    }

    /// <summary>A class, not a tuple, so "the user cancelled" can be told from "it failed".</summary>
    private sealed record SendOutcome(bool Ok, string Message);

    private void ShowBackups()
    {
        if (_engine is null || _location is null) return;

        var saves = SaveDiscovery.Enumerate(_location).Where(s => s.HasPassport).ToList();
        if (saves.Count == 0)
        {
            Dialogs.Info(this, "Backups",
                "There are no backups yet. One is kept automatically whenever a save gets replaced.");
            return;
        }

        var slot = saves.Count == 1 ? saves[0] : PickSave(saves);
        if (slot is null) return;

        using var form = new BackupsForm(_engine, slot);
        form.ShowDialog(this);
        if (form.Changed) Rebuild();
    }

    private SaveSlot? PickSave(List<SaveSlot> saves)
    {
        using var dlg = new Form
        {
            Text = "Which save?",
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(420, 40 + saves.Count * 46),
        };

        SaveSlot? picked = null;
        int y = 20;
        foreach (var s in saves)
        {
            var b = new FlatButton(s.Display) { Width = 372, Height = 40 };
            b.SetBounds(24, y, 372, 40);
            var captured = s;
            b.Click += (_, _) => { picked = captured; dlg.DialogResult = DialogResult.OK; dlg.Close(); };
            dlg.Controls.Add(b);
            y += 46;
        }

        dlg.HandleCreated += (_, _) => Theme.UseDarkTitleBar(dlg);
        dlg.ShowDialog(this);
        return picked;
    }

    private void SwitchProfile()
    {
        if (!Dialogs.Confirm(this, "Switch to someone else",
                "Close and go back to the name list?", "Go back")) return;

        _config.LastProfileId = null;
        try { _config.Save(); } catch (IOException) { }

        // Deliberately not Application.Restart: that reuses the original command line, so a copy
        // started with Windows would come back in background mode, quietly pick the first name on
        // the list, and never show the picker the user just asked for.
        _reallyClosing = true;

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = _stickRoot is not null && Directory.Exists(_stickRoot) ? $"\"{_stickRoot}\"" : "",
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // If the relaunch fails they can simply open it again themselves.
        }

        Close();
    }

    private void ChooseStick()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose the USB stick or folder used to carry saves",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (_stickRoot is not null && Directory.Exists(_stickRoot)) dlg.SelectedPath = _stickRoot;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _stickRoot = dlg.SelectedPath;
        Rebuild();
    }

    private void ChooseSavesFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Find your 7DaysToDie folder (the one with Saves inside it)",
            UseDescriptionForTitle = true,
        };

        var guess = GamePaths.UserDataCandidates().FirstOrDefault();
        if (guess is not null) dlg.SelectedPath = guess.Path;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (!GamePaths.LooksLikeUserData(dlg.SelectedPath))
        {
            Dialogs.Warn(this, "Not the right folder",
                $"That folder does not look like a 7 Days to Die save folder.\n\n{dlg.SelectedPath}\n\n"
                + "The right one is usually called 7DaysToDie and has a Saves folder inside it.");
            return;
        }

        _config.UserDataOverride = dlg.SelectedPath;
        _config.Save();
        Startup();
    }

    // ------------------------------------------------------------------ notices

    private void Notice(Severity level, string headline, string body)
    {
        _notices.Controls.Add(new Banner(level, headline, body)
        {
            Width = ClientSize.Width - PadX * 2 - 6,
        });
        Relayout();
    }

    private void ClearNotices()
    {
        foreach (Control c in _notices.Controls.Cast<Control>().ToList())
        {
            _notices.Controls.Remove(c);
            c.Dispose();
        }
    }

    // ------------------------------------------------------------------ shutdown

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window when installed leaves it running quietly, which is the whole point
        // of installing it. Quit from the tray menu actually exits.
        if (!_reallyClosing && e.CloseReason == CloseReason.UserClosing && Installer.RunningInstalled)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(4000, "Still running",
                "Save Transfer is in the notification area, keeping an eye on the other PC.", ToolTipIcon.Info);
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ActivityLog.Write("closed");
        if (_stickRoot is not null) ActivityLog.MirrorTo(_stickRoot);

        _stopShowListener = true;
        try { _showRequest?.Set(); } catch (ObjectDisposedException) { }
        _showRequest?.Dispose();

        _watchTimer.Stop();
        _watchTimer.Dispose();
        _housekeeping.Stop();
        _housekeeping.Dispose();
        _watcher?.Dispose();
        _discovery?.Dispose();
        _server?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }
}
