using System.Text;
using SaveSync.Core;
using SaveSync.Core.Lan;

namespace SaveSync.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        if (Has("--diagnose")) return Diagnose();

        // Runs elevated, does one job, exits. This is what turns the alarming Windows firewall
        // popup into a single ordinary Yes.
        if (Has("--setup-network")) return SetupNetwork(args);

        ApplicationConfiguration.Initialize();

        // Waits a few seconds rather than giving up instantly: switching person relaunches the
        // program, and the replacement would otherwise race the copy that is still shutting down
        // and be told it is already running.
        using var single = new Mutex(false, "SaveSync.7DaysToDie.SingleInstance");
        _singleInstance = single;

        // A copy started BY an update handover waits far longer for the slot than one started by a
        // person double-clicking.
        //
        // The old copy launches the new one and only then begins shutting down, so for a moment
        // both exist and the slot is still held. Five seconds is plenty for somebody's second
        // double-click and nowhere near enough for a shutdown that has a log to flush to a USB
        // stick - and losing that race leaves the machine with NOTHING running, on a PC nobody is
        // sitting at, which is the one outcome worth engineering against.
        var slotWait = Has("--handover") ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(5);

        bool held;
        try { held = single.WaitOne(slotWait); }
        catch (AbandonedMutexException) { held = true; } // the previous copy died; the slot is ours

        if (!held)
        {
            // An OLDER copy sitting in the tray is the usual reason somebody runs a newer one, and
            // the single-instance rule would otherwise hand them the old window and look like
            // nothing happened. Offer the update instead of silently doing the wrong thing.
            if (Installer.IsInstalled && Installer.InstalledIsOlder && !Installer.RunningInstalled)
            {
                var installed = Installer.InstalledVersion;
                var answer = MessageBox.Show(
                    $"This PC is running an older version ({installed}) in the background, and you have "
                    + $"just started a newer one ({Installer.ThisVersion}).\n\n"
                    + "Update this PC to the newer version?\n\n"
                    + "Nothing about your saves or backups changes - only the program itself.",
                    "Update Save Transfer",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);

                if (answer == DialogResult.Yes)
                {
                    try
                    {
                        Installer.Install();          // stops the old copy, then replaces it
                        single.Dispose();             // the slot is free now the old one is gone
                        Installer.LaunchInstalled();
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            "Could not update this PC: " + ex.Message
                            + "\n\nThe older version is still installed and still works.",
                            "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return 1;
                    }
                }
            }

            // A handover that cannot get the slot carries on anyway.
            //
            // Two copies running is untidy - the second cannot take the network port and says so.
            // Nothing running is unreachable, and on a machine nobody sits at that is unrecoverable
            // without somebody walking over to it. Untidy beats unreachable every time.
            if (Has("--handover"))
            {
                try
                {
                    var where = Path.Combine(Path.GetTempPath(), "savesync-handover-slot.txt");
                    File.WriteAllText(where,
                        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  handover to {Installer.ThisVersion} "
                        + $"could not take the single-instance slot after {slotWait.TotalSeconds:0}s; "
                        + "starting anyway rather than leaving this PC with nothing running."
                        + Environment.NewLine);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

                held = true;
            }
        }

        if (!held)
        {

            // Already running - most likely the installed copy sitting in the tray. Bring that one
            // to the front rather than telling them to go and find it.
            try
            {
                if (EventWaitHandle.TryOpenExisting(MainForm.ShowRequestEventName, out var show))
                {
                    using (show) show.Set();
                    return 0;
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }

            MessageBox.Show(
                "Save Transfer is already running. Look for it next to the clock, at the right-hand "
                + "end of the taskbar.",
                "Already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        try
        {
            return Run(args);
        }
        finally
        {
            ReleaseSingleInstanceSlot();
        }
    }

    private static int Run(string[] args)
    {
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        AppConfig config;
        try
        {
            config = AppConfig.Load();
            config.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read settings:\n\n{ex.Message}", "Save Transfer",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            config = new AppConfig();
        }

        // A folder argument names the stick explicitly, so a drive can be dragged onto the EXE.
        var initial = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal) && Directory.Exists(a));
        bool background = Has("--background");

        var store = ProfileStore.Load();

        // Started with Windows and we already know who it was last time: straight to the tray, no
        // questions. Otherwise ask who is using it, which is always the first thing they see.
        var profile = background ? store.ById(config.LastProfileId) ?? store.Profiles.FirstOrDefault() : null;

        if (profile is null)
        {
            using var picker = new ProfileForm(store, config.LastProfileId);
            picker.ShowDialog();
            if (picker.Selected is null) return 0;

            profile = picker.Selected;
            config.LastProfileId = profile.Id;
            try { config.Save(); } catch (IOException) { }
            background = false;
        }

        Application.Run(new MainForm(config, profile, initial, startHidden: background));
        return 0;
    }

    private static int SetupNetwork(string[] args)
    {
        int tcp = LanProtocol.DefaultPort;
        int udp = LanProtocol.DiscoveryPort;

        var numbers = args.Where(a => int.TryParse(a, out _)).Select(int.Parse).ToArray();
        if (numbers.Length >= 1) tcp = numbers[0];
        if (numbers.Length >= 2) udp = numbers[1];

        return FirewallSetup.Configure(tcp, udp) ? 0 : 1;
    }

    /// <summary>
    /// Prints what the program can see. Exists so a non-technical user can be asked to run one
    /// command and send back the result when something is not where it was expected.
    /// </summary>
    private static Mutex? _singleInstance;
    private static bool _slotReleased;

    /// <summary>
    /// Gives up the single-instance slot before handing over to a newer copy.
    ///
    /// Without this the handover is a race with itself: the old copy starts the new one and only
    /// then begins shutting down, so the new copy arrives to find the slot still taken and has to
    /// outwait a shutdown of unknown length. Losing that race leaves the machine with nothing
    /// running at all - which is precisely what happened to two PCs at once. Letting go of the
    /// slot first removes the race rather than widening it.
    /// </summary>
    public static void ReleaseSingleInstanceSlot()
    {
        if (_slotReleased) return;
        _slotReleased = true;

        try { _singleInstance?.ReleaseMutex(); }
        catch (Exception e) when (e is ApplicationException or ObjectDisposedException) { }
    }

    private static int Diagnose()
    {
        var sb = new StringBuilder();
        sb.AppendLine("7 Days to Die - Save Transfer diagnostics");
        sb.AppendLine($"tool version : {TransferEngine.ToolVersion}");
        sb.AppendLine($"machine      : {Machine.Name}");
        sb.AppendLine($"user         : {Machine.User}");
        sb.AppendLine($"running from : {AppContext.BaseDirectory}");
        sb.AppendLine($"config       : {AppFolders.ConfigPath} (portable={AppFolders.IsPortable})");
        sb.AppendLine($"  beside exe : {AppFolders.PortableConfigPath} (writable={AppFolders.CanWriteBesideProgram()})");
        sb.AppendLine($"  per user   : {AppFolders.LocalConfigPath}");
        sb.AppendLine($"installed    : {Installer.IsInstalled} (startup={Installer.StartsWithWindows})");
        sb.AppendLine($"network rules: {FirewallSetup.IsConfigured()}");
        sb.AppendLine($"on a stick   : {StickLocator.IsRunningFromStick()}");
        sb.AppendLine();

        sb.AppendLine("people on this copy:");
        foreach (var p in ProfileStore.Load().Profiles) sb.AppendLine($"  {p.Name}");
        sb.AppendLine();

        sb.AppendLine($"steam root   : {SteamLocator.FindSteamRoot() ?? "(not found)"}");
        foreach (var lib in SteamLocator.LibraryFolders()) sb.AppendLine($"  library    : {lib}");
        foreach (var opt in SteamLocator.LaunchOptions(SteamLocator.SevenDaysAppId)) sb.AppendLine($"  launchopts : {opt}");
        sb.AppendLine();

        sb.AppendLine("save folder candidates:");
        foreach (var c in GamePaths.UserDataCandidates())
            sb.AppendLine($"  [{(c.Valid ? "OK " : "no ")}] {c.Path}  <- {c.Provenance}");
        sb.AppendLine();

        sb.AppendLine("drives that could be the stick:");
        foreach (var c in StickLocator.Candidates())
            sb.AppendLine($"  {c.Root}  {c.Label}  saves={c.HasSaves}  <- {c.Why}");
        sb.AppendLine();

        var config = AppConfig.Load();
        var loc = config.Locate();
        if (loc is null)
        {
            sb.AppendLine("RESULT: save folder NOT FOUND.");
        }
        else
        {
            sb.AppendLine($"user data    : {loc.UserDataRoot}  ({loc.Provenance})");
            sb.AppendLine($"install      : {loc.InstallDir ?? "(not found)"}");
            sb.AppendLine($"game running : {GamePaths.IsGameRunning()}");
            sb.AppendLine();

            sb.AppendLine("saves:");
            foreach (var s in SaveDiscovery.Enumerate(loc))
            {
                sb.AppendLine($"  {s.SaveName} ({s.World})  {PathUtil.HumanBytes(s.SizeBytes)}  {s.WorldKind}");
                sb.AppendLine(s.Passport is null
                    ? "      never copied anywhere"
                    : $"      v{s.Passport.Ordinal} {s.Passport.VersionId} last from {s.Passport.LastPlayedOn}");
            }

            // Mods and backups are in here because "it did not bring my mods" and "where did my
            // backup go" are the two questions this report exists to answer without a phone call.
            sb.AppendLine();
            sb.AppendLine($"mods (carrying them is {(config.IncludeMods ? "ON" : "OFF")}):");
            sb.AppendLine($"  user data : {Mods.UserDataModsDir(loc)}");
            sb.AppendLine($"  install   : {Mods.InstallModsDir(loc) ?? "(install not found)"}");
            try
            {
                var mods = Mods.Enumerate(loc);
                if (mods.Count == 0) sb.AppendLine("  (none found)");
                foreach (var m in mods)
                {
                    Mods.Measure(m);
                    sb.AppendLine($"  {m.Label} {m.Version}  {PathUtil.HumanBytes(m.SizeBytes)}  {m.FileCount} files  [{m.Root}]"
                        + (m.ShippedWithGame ? "  (came with the game - not copied between PCs)" : ""));
                    sb.AppendLine($"      {m.Folder}");
                    if (m.IsAwkwardlyNested)
                        sb.AppendLine($"      note: {m.NestedDepth - 1} extra folder(s) around it - found anyway, and copied across properly");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                sb.AppendLine($"  could not read the mods folder: {e.Message}");
            }

            sb.AppendLine();
            try
            {
                // Read-only on purpose. A report that creates the folders it is reporting on is a
                // report you cannot trust, and running --diagnose must never change anything.
                var ws = new Workspace(loc.UserDataRoot);
                if (!Directory.Exists(ws.Snapshots))
                {
                    sb.AppendLine("backups: none yet (nothing has been transferred on this PC).");
                    throw new DiagnosticsDone();
                }

                var store = new SnapshotStore(ws, config.SnapshotsToKeep);
                var all = store.ListAll();
                sb.AppendLine($"backups ({all.Count}, retention = "
                    + $"{(store.KeepsEverything ? "keep everything" : config.SnapshotsToKeep + " per save")}):");
                foreach (var b in all)
                    sb.AppendLine($"  {b.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}  {b.SaveName} ({b.World})  "
                        + $"{PathUtil.HumanBytes(b.SizeBytes)}{(b.Pinned ? "  PINNED" : "")}  {b.Reason}");
            }
            catch (DiagnosticsDone) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                sb.AppendLine($"backups: could not read them: {e.Message}");
            }
        }

        // The tail of the log last, so a report pasted anywhere ends with what actually happened.
        sb.AppendLine();
        sb.AppendLine("known other PCs:");
        foreach (var peer in config.Peers)
            sb.AppendLine($"  {peer.DisplayName} ({peer.MachineId}) paired={peer.IsPaired} last={peer.LastAddress}");

        var logPath = loc is null ? null : Path.Combine(new Workspace(loc.UserDataRoot).Logs, ActivityLog.FileName);
        sb.AppendLine();
        if (logPath is not null && File.Exists(logPath))
        {
            sb.AppendLine($"what it has been doing ({logPath}), most recent last:");
            try
            {
                foreach (var line in File.ReadLines(logPath).TakeLast(60)) sb.AppendLine("  " + line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                sb.AppendLine("  could not read it: " + e.Message);
            }
        }
        else
        {
            sb.AppendLine("what it has been doing: nothing recorded yet.");
        }

        var text = sb.ToString();
        Console.WriteLine(text);

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "savesync-diagnostics.txt");
            File.WriteAllText(path, text);
            Console.WriteLine($"\nAlso written to: {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        return 0;
    }

    /// <summary>Local control flow only: "this section has said all it needs to".</summary>
    private sealed class DiagnosticsDone : Exception { }

    private static void ReportCrash(Exception? ex)
    {
        if (ex is null) return;

        string? logPath = null;
        try
        {
            var dir = AppFolders.ConfigDir;
            Directory.CreateDirectory(dir);
            logPath = Path.Combine(dir, "crash.log");
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O}\n{ex}\n\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        MessageBox.Show(
            "Something went wrong.\n\n"
            + ex.Message
            + "\n\nYour saves were not changed - this tool only ever writes after checking every file."
            + (logPath is not null ? $"\n\nDetails saved to:\n{logPath}" : ""),
            "Save Transfer", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
