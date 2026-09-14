using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace SaveSync.Core;

/// <summary>
/// Starting the game on a PC nobody is sitting at, in a named save.
///
/// This was written off once as impossible - "Steam can be told to start the game, and nothing can
/// be told which world to load". That was wrong, and it was wrong in the way worth remembering: it
/// described what THIS program could do and reported it as a fact about the game. The game has
/// carried the machinery all along, and it is not a hack or a mod - it is how the game restarts
/// itself back into your world after it changes a setting that needs a restart.
///
/// Three arguments, read out of the game's own code rather than guessed at:
///
///     -world=&lt;World&gt;    sets the GameWorld preference     (GameStartupHelper.parseRawCommandline)
///     -name=&lt;Save&gt;      sets the GameName preference      (same)
///     -LoadSaveGame=true                                  (LaunchPrefs, a plain bool)
///
/// The last one switches on a small state machine - Platform.PlatformApplicationManager.LoadSaveGame
/// - which looks up that world and save among the ones on the PC and then works the menu itself:
///
///     ContinueGameOpen -> ContinueGameSelect -> ContinueGamePlay
///
/// It says what it is doing as it goes, so the result is readable afterwards without being there:
///
///     [LoadSaveGame] Loading existing save game 'Chris Main Save' (world 'Navezgane').
///     [LoadSaveGame] Creating new save game '...' from the world '...'.
///     [LoadSaveGame] Can not load archived save '...' (world '...').
///
/// Note the middle one. A name that matches nothing is not an error to the game - it cheerfully
/// makes a brand new world under that name. That is the one genuinely destructive-feeling outcome
/// available here, so a save that is not there is refused before anything is started.
///
/// The arguments require starting the game's executable rather than asking Steam to start it,
/// because a steam:// URL carries no arguments. That means taking over a job the game's own
/// launcher normally does: reading launchersettings.json and turning it into flags. Getting that
/// wrong would silently change how somebody's game runs - most importantly it could switch
/// EasyAntiCheat back on for a person who deliberately turned it off. So this does not invent a
/// launch; it repeats the one that machine last used, read from the launcher's own log, and only
/// falls back to deriving one from the settings file when that PC has no log to copy.
/// </summary>
public static class GameLauncher
{
    public const string LauncherSettingsFile = "launchersettings.json";
    public const string LauncherLogFile = "launcher.log";

    /// <summary>
    /// Switches on the game's own load-a-save automation. A bool, not the name of the save.
    ///
    /// Worth stating because the obvious reading is wrong and costs a launch to discover: passing
    /// the save name here gets "Could not parse config value", because LaunchPrefs parses it with
    /// bool.TryParse. The save is named by -world= and -name=.
    /// </summary>
    public const string LoadSaveArg = "-LoadSaveGame=";

    public const string WorldArg = "-world=";
    public const string NameArg = "-name=";

    /// <summary>
    /// Skips the "press Spawn to enter the world" step, which is the last click in the way.
    ///
    /// Loading the save is not the same as being in it. The game loads the world and then waits,
    /// in GameManager, on a button:
    ///
    ///     if (!GamePrefs.GetBool(EnumGamePrefs.SkipSpawnButton) &amp;&amp; !IsEditMode())
    ///     {
    ///         canSpawnPlayer = false;
    ///         XUiC_SpawnSelectionWindow.Open(...);
    ///         while (!canSpawnPlayer) yield return null;
    ///     }
    ///
    /// There is no automating that button - the only auto-press in the game is gated on its
    /// AutomationRunner, and the script loader for that logs "Disabled for this build type" in
    /// retail. But the gate itself is a preference, and the preference is the game's own.
    ///
    /// It affects only this gate. Choosing where to respawn after dying is a different window,
    /// opened with _chooseSpawnPosition: true, and is untouched by this.
    /// </summary>
    public const string SkipSpawnArg = "-SkipSpawnButton=true";

    /// <summary>Where the game keeps its preferences on Windows. Unity PlayerPrefs, so the registry.</summary>
    public const string PrefsKeyPath = @"Software\The Fun Pimps\7 Days To Die";

    /// <summary>Unity stores a preference as "Name_h" plus a hash, so it is found by prefix.</summary>
    public const string SkipSpawnPrefPrefix = "SkipSpawnButton_h";

    // ------------------------------------------------------------------ settings

    /// <summary>
    /// How this PC is set up to start the game, as its own launcher would.
    ///
    /// EAC matters most and is the reason this is read at all rather than assumed: it is off on
    /// both of these machines by choice, and a launch that quietly turned it back on would change
    /// the thing the person had deliberately set.
    /// </summary>
    public sealed class LauncherSettings
    {
        public bool UseEac { get; init; }
        public string Renderer { get; init; } = "dx11";
        public bool GraphicsJobs { get; init; } = true;
        public bool ExclusiveMode { get; init; }
        public bool UseNativeInput { get; init; } = true;
        public string AdditionalParameters { get; init; } = "";

        /// <summary>Where these came from, so a surprising launch can be traced without guessing.</summary>
        public string Provenance { get; init; } = "defaults (no launchersettings.json)";

        public string Describe() =>
            $"EAC {(UseEac ? "ON" : "off")}, renderer {Renderer}"
            + (ExclusiveMode ? ", exclusive fullscreen" : "")
            + (UseNativeInput ? "" : ", native input off")
            + (string.IsNullOrWhiteSpace(AdditionalParameters) ? "" : $", extra \"{AdditionalParameters.Trim()}\"");
    }

    // The on-disk shape. Nested exactly as the launcher writes it; Json.Options is
    // case-insensitive, so the file's PascalCase reads straight into these.
    private sealed class SettingsFile
    {
        public RunConfig? DefaultRunConfig { get; set; }
    }

    private sealed class RunConfig
    {
        public bool GraphicsJobs { get; set; } = true;
        public bool ExclusiveMode { get; set; }
        public string? Renderer { get; set; }
        public bool UseEac { get; set; }
        public bool UseNativeInput { get; set; } = true;
        public string? AdditionalParameters { get; set; }
    }

    public static LauncherSettings ReadSettings(GameLocation location)
    {
        var path = Path.Combine(location.UserDataRoot, LauncherSettingsFile);
        var file = Json.ReadFile<SettingsFile>(path);
        if (file?.DefaultRunConfig is null) return new LauncherSettings();

        var c = file.DefaultRunConfig;
        return new LauncherSettings
        {
            UseEac = c.UseEac,
            Renderer = string.IsNullOrWhiteSpace(c.Renderer) ? "dx11" : c.Renderer!.Trim(),
            GraphicsJobs = c.GraphicsJobs,
            ExclusiveMode = c.ExclusiveMode,
            UseNativeInput = c.UseNativeInput,
            AdditionalParameters = c.AdditionalParameters ?? "",
            Provenance = LauncherSettingsFile,
        };
    }

    // ------------------------------------------------------------------ the plan

    /// <summary>Exactly what will be run, decided before anything is started so it can be logged.</summary>
    public sealed record Plan(string Exe, List<string> Arguments, string LogFile, string Basis)
    {
        public string CommandLine =>
            Quote(Exe) + " " + string.Join(" ", Arguments.Select(Quote));

        private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
    }

    /// <summary>
    /// The arguments the launcher itself last used on this PC, read out of its log.
    ///
    /// This is the honest answer to "how does this machine start the game": not a mapping this
    /// program invented from a settings file, but the exact line that machine's own launcher
    /// produced the last time a person started it. It already carries EAC, the renderer, and
    /// anything typed into the extra-parameters box.
    ///
    /// Returns null when the game has never been started on this PC.
    /// </summary>
    public static (string Exe, List<string> Args)? LastLauncherInvocation(GameLocation location)
    {
        var path = Path.Combine(location.UserDataRoot, "logs", LauncherLogFile);

        string[] lines;
        try
        {
            if (!File.Exists(path)) return null;
            lines = File.ReadAllLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // The log is appended to on every launch, so the LAST block is the current one.
        //
        //   Executing: C:\...\7DaysToDie.exe
        //       -force-d3d11
        //       -nogs
        //       -noeac
        //       -logfile
        //       C:\Users\...\logs\output_log_client__2026-09-13__16-23-49.txt
        //
        //   Launched the game executable
        const string marker = "Executing:";

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            int at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;

            var exe = line[(at + marker.Length)..].Trim();
            if (exe.Length == 0) continue;

            var args = new List<string>();
            for (int j = i + 1; j < lines.Length; j++)
            {
                var raw = lines[j];
                // The block is indented; the first line that is not ends it.
                if (raw.Length == 0 || !char.IsWhiteSpace(raw[0])) break;

                var arg = raw.Trim();
                if (arg.Length > 0) args.Add(arg);
            }

            return File.Exists(exe) ? (exe, args) : null;
        }

        return null;
    }

    /// <summary>
    /// Derives a launch from the settings file, for a PC where the game has never been started.
    ///
    /// Every flag here was read off a real launcher invocation rather than guessed at, and the
    /// names all appear in 7dLauncher.exe itself. Where this and LastLauncherInvocation disagree,
    /// the log wins - it is evidence, and this is a reconstruction.
    /// </summary>
    public static (string Exe, List<string> Args) DeriveInvocation(string installDir, LauncherSettings settings)
    {
        // EAC is a different executable, not a flag. Falling back to the plain one when the EAC
        // build is missing is deliberate: not launching at all would be the worse failure.
        var eacExe = Path.Combine(installDir, "7DaysToDie_EAC.exe");
        var plainExe = Path.Combine(installDir, GamePaths.ProcessName + ".exe");
        var exe = settings.UseEac && File.Exists(eacExe) ? eacExe : plainExe;

        var args = new List<string>
        {
            settings.Renderer.ToLowerInvariant() switch
            {
                "dx12" => "-force-d3d12",
                "glcore" => "-force-glcore",
                "vulkan" => "-force-vulkan",
                _ => "-force-d3d11",
            },
            "-nogs",
        };

        if (!settings.UseEac) args.Add("-noeac");
        if (!settings.UseNativeInput) args.Add("-disablenativeinput");
        if (settings.ExclusiveMode) { args.Add("-window-mode"); args.Add("exclusive"); }

        foreach (var extra in SplitArguments(settings.AdditionalParameters)) args.Add(extra);

        return (exe, args);
    }

    /// <summary>Splits the extra-parameters box the way a command line would, honouring quotes.</summary>
    public static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var current = new StringBuilder();
        bool quoted = false;

        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Works out exactly how to start the game on this PC, loading <paramref name="saveName"/>.
    ///
    /// Pass a null world and save to start it the way a person would and leave it at its menu.
    /// </summary>
    public static Plan PlanLaunch(GameLocation location, string installDir, string? world, string? saveName)
    {
        var fromLog = LastLauncherInvocation(location);
        var settings = ReadSettings(location);

        var (exe, args) = fromLog is not null
            ? (fromLog.Value.Exe, new List<string>(fromLog.Value.Args))
            : DeriveInvocation(installDir, settings);

        // Describing the launch by the settings file was wrong in a way that mattered: both of
        // these machines have a settings file claiming EasyAntiCheat is on while every real launch
        // carries -noeac, so every message said "EAC ON" about a machine running without it. When
        // a real launch is being repeated, it describes itself.
        var basis = fromLog is not null
            ? $"repeating this PC's last launch ({DescribeInvocation(exe, args)})"
            : $"built from {settings.Provenance} ({settings.Describe()})";

        // Anything that decides where the log goes or which save loads is ours to set, so strip
        // whatever the previous launch left behind rather than passing it twice.
        args = StripArg(args, "-logfile", takesValue: true);
        args = StripArg(args, "-logpath", takesValue: true);
        args = StripArg(args, LoadSaveArg, takesValue: false);
        args = StripArg(args, WorldArg, takesValue: false);
        args = StripArg(args, NameArg, takesValue: false);

        // Same folder and same name shape the launcher uses, because the machine report finds the
        // frame rate by reading the newest output_log*.txt - a log written anywhere else would be
        // invisible to every other part of this program.
        var logFile = Path.Combine(
            location.UserDataRoot, "logs",
            $"output_log_client__{DateTime.Now:yyyy-MM-dd__HH-mm-ss}.txt");

        args.Add("-logfile");
        args.Add(logFile);

        if (!string.IsNullOrWhiteSpace(saveName))
        {
            // The world and the name say WHICH save; the bool is what makes the game go and get
            // it instead of stopping at its menu. All three, or none of them.
            if (!string.IsNullOrWhiteSpace(world)) args.Add(WorldArg + world);
            args.Add(NameArg + saveName);
            args.Add(LoadSaveArg + "true");

            // And the last click: loading the world is not the same as being in it.
            args = StripArg(args, "-SkipSpawnButton=", takesValue: false);
            args.Add(SkipSpawnArg);
        }

        return new Plan(exe, args, logFile, basis);
    }

    /// <summary>What a launch actually does, said in terms of the launch rather than a settings file.</summary>
    public static string DescribeInvocation(string exe, IEnumerable<string> args)
    {
        var list = args.ToList();

        var eac = Path.GetFileName(exe).Contains("_EAC", StringComparison.OrdinalIgnoreCase) ? "EAC ON"
                : list.Any(a => a.Equals("-noeac", StringComparison.OrdinalIgnoreCase)) ? "EAC off"
                : "EAC not specified";

        var renderer = list.FirstOrDefault(a => a.StartsWith("-force-", StringComparison.OrdinalIgnoreCase));

        return renderer is null ? eac : $"{eac}, {renderer[7..]}";
    }

    private static List<string> StripArg(List<string> args, string name, bool takesValue)
    {
        var kept = new List<string>(args.Count);

        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];

            bool match = name.EndsWith('=')
                ? a.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(a, name, StringComparison.OrdinalIgnoreCase);

            if (!match) { kept.Add(a); continue; }
            if (takesValue) i++;   // and its value
        }

        return kept;
    }

    // ------------------------------------------------------------------ putting it back

    /// <summary>
    /// What the spawn-button preference was before we touched it.
    ///
    /// It has to be captured, because the game persists that preference: it is declared with the
    /// StandaloneWindows flag, so it is written to the registry when the game exits and would stay
    /// changed for every launch afterwards. Somebody starting the game themselves next week would
    /// find a step missing and no explanation for it. Borrowing a setting is fine; keeping it is
    /// not.
    ///
    /// Absent is a real state, and the common one - the preference has never been written on
    /// either of these machines. Restoring "absent" means deleting whatever the game wrote.
    /// </summary>
    public sealed record SpawnPrefBackup(string? ValueName, object? Data, RegistryValueKind Kind)
    {
        public bool WasAbsent => ValueName is null;
    }

    /// <summary>
    /// Where a pending restore is written down, so it survives this program stopping.
    ///
    /// The restore used to live only in a background task, which meant a program that was updated,
    /// restarted or killed while the game was running left the borrowed setting behind forever -
    /// and this program is restarted remotely as a matter of routine. A note on disk is read back
    /// at startup and the setting put right then.
    /// </summary>
    public static string PendingRestorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SaveSync", "spawn-pref-restore.json");

    /// <summary>The written-down form. Kept flat so a person can read it and undo it by hand.</summary>
    public sealed class PendingRestore
    {
        public string? ValueName { get; set; }
        public string Kind { get; set; } = "";
        public int? Number { get; set; }
        public string? Text { get; set; }
        public string? Base64 { get; set; }
        public DateTimeOffset WrittenAt { get; set; } = DateTimeOffset.Now;
    }

    public static void WriteDownRestore(SpawnPrefBackup? backup)
    {
        if (backup is null) return;

        try
        {
            var note = new PendingRestore { ValueName = backup.ValueName, Kind = backup.Kind.ToString() };

            switch (backup.Data)
            {
                case int i: note.Number = i; break;
                case long l: note.Number = (int)l; break;
                case string s: note.Text = s; break;
                case byte[] b: note.Base64 = Convert.ToBase64String(b); break;
            }

            Json.WriteFileAtomic(PendingRestorePath, note);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A restore that cannot be written down is still attempted in this process.
        }
    }

    public static void ForgetRestore()
    {
        try { File.Delete(PendingRestorePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Puts back a setting borrowed by a copy of this program that is no longer running.
    ///
    /// Called at startup. Waits for the game to be closed first, because putting it back while the
    /// game is up would be undone the moment the game writes its preferences on exit.
    /// </summary>
    public static void RestorePendingSpawnPref()
    {
        PendingRestore? note;
        try
        {
            if (!File.Exists(PendingRestorePath)) return;
            note = Json.ReadFile<PendingRestore>(PendingRestorePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (note is null) { ForgetRestore(); return; }

        object? data = note.Number is not null ? note.Number.Value
                     : note.Text is not null ? note.Text
                     : note.Base64 is not null ? Convert.FromBase64String(note.Base64)
                     : null;

        var kind = Enum.TryParse<RegistryValueKind>(note.Kind, out var k) ? k : RegistryValueKind.Unknown;
        var backup = new SpawnPrefBackup(note.ValueName, data, kind);

        ActivityLog.Write($"a spawn-button setting borrowed at {note.WrittenAt:HH:mm} is still out; "
                          + "the copy of this program that borrowed it is gone");

        // Putting it back while the game is up would be undone the moment the game writes its
        // preferences on the way out, so this waits rather than doing it now and being wrong.
        if (GamePaths.IsGameRunning())
        {
            ActivityLog.Write("  the game is still running, so it goes back when the game closes");
            WaitForGameThenRestore(backup, alreadyStarted: true);
            return;
        }

        RestoreSpawnPref(backup);
        ForgetRestore();
    }

    /// <summary>Whether this PC is currently set to skip the spawn button. Null when never set.</summary>
    public static bool? SpawnButtonSkipped()
    {
        var found = BackupSpawnPref();
        if (found is null || found.WasAbsent) return null;

        return found.Data switch
        {
            int i => i != 0,
            long l => l != 0,
            string s => s is "1" or "true" or "True",
            _ => null,
        };
    }

    /// <summary>
    /// Gives the borrowed setting back unconditionally, whatever state it got left in.
    ///
    /// The undo for this whole feature, and it exists because the feature needed it for real: a
    /// launch borrowed the setting, this program was updated and restarted before it could hand it
    /// back, and the machine was left changed with nothing recorded about it. Anything that
    /// borrows something on somebody else's PC should ship with the button that gives it back.
    ///
    /// Deleting rather than writing a zero, because "never set" is what it was, and a value that
    /// was not there before should not be there afterwards.
    /// </summary>
    public static (bool Ok, string Message) GiveBackSpawnPref()
    {
        if (GamePaths.IsGameRunning())
            return (false, "The game is running on this PC. It writes its settings when it closes, "
                           + "so this would be undone. Close it first.");

        var before = SpawnButtonSkipped();
        ForgetRestore();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PrefsKeyPath, writable: true);
            if (key is null) return (true, "This PC has no game settings to put back.");

            var names = key.GetValueNames()
                .Where(n => n.StartsWith(SkipSpawnPrefPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var n in names) key.DeleteValue(n, throwOnMissingValue: false);

            ActivityLog.Write($"gave the spawn-button setting back (it was {Describe(before)})");

            return (true, names.Count == 0
                ? "It was already how it started - nothing to put back."
                : $"Put back. The spawn screen behaves as it did before ({Describe(before)} until now).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (false, "Could not put it back: " + e.Message);
        }

        static string Describe(bool? v) => v switch
        {
            true => "set to skip the spawn screen",
            false => "set to show the spawn screen",
            _ => "never set",
        };
    }

    public static SpawnPrefBackup? BackupSpawnPref()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PrefsKeyPath);
            if (key is null) return null;

            var name = key.GetValueNames()
                .FirstOrDefault(n => n.StartsWith(SkipSpawnPrefPrefix, StringComparison.OrdinalIgnoreCase));

            return name is null
                ? new SpawnPrefBackup(null, null, RegistryValueKind.Unknown)
                : new SpawnPrefBackup(name, key.GetValue(name), key.GetValueKind(name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Puts the spawn-button preference back exactly as it was found.</summary>
    public static void RestoreSpawnPref(SpawnPrefBackup? backup)
    {
        if (backup is null) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PrefsKeyPath, writable: true);
            if (key is null) return;

            if (backup.WasAbsent)
            {
                foreach (var n in key.GetValueNames()
                             .Where(n => n.StartsWith(SkipSpawnPrefPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    key.DeleteValue(n, throwOnMissingValue: false);
                }

                ActivityLog.Write("put the spawn-button setting back to how it was (it had never been set)");
                return;
            }

            if (backup.Data is not null) key.SetValue(backup.ValueName!, backup.Data, backup.Kind);
            ActivityLog.Write("put the spawn-button setting back to how it was");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ActivityLog.Write("could not put the spawn-button setting back", e);
        }
    }

    /// <summary>
    /// Waits for the game to finish, then puts the borrowed preference back.
    ///
    /// Polled rather than waited on, because the process that is started is not always the process
    /// that ends up running: with EasyAntiCheat on, the executable we start hands over to another
    /// one, and waiting on our own handle would restore the setting while the game was still
    /// loading - which would put the spawn screen back in front of a machine nobody is sitting at.
    ///
    /// Capped, so a game left running for days does not leave a task waiting for it forever. The
    /// setting is put back either way.
    /// </summary>
    private static void RestoreWhenTheGameFinishes(SpawnPrefBackup? backup)
        => WaitForGameThenRestore(backup, alreadyStarted: false);

    /// <summary>
    /// Waits for the game to finish, then puts the borrowed preference back.
    ///
    /// <paramref name="alreadyStarted"/> distinguishes the two callers. Just after a launch the
    /// game has not appeared yet, so "not running" means "not yet" and has to be waited through.
    /// Picking up somebody else's note at startup, the game is already up, and waiting for it to
    /// start again would be waiting for something that has already happened.
    /// </summary>
    private static void WaitForGameThenRestore(SpawnPrefBackup? backup, bool alreadyStarted)
    {
        if (backup is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!alreadyStarted)
                {
                    // Give it room to start: it takes minutes on the slower of these two machines,
                    // and "not running yet" must not be read as "already finished".
                    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
                    while (DateTimeOffset.UtcNow < deadline && !GamePaths.IsGameRunning())
                        await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }

                var giveUp = DateTimeOffset.UtcNow + TimeSpan.FromHours(16);
                while (DateTimeOffset.UtcNow < giveUp && GamePaths.IsGameRunning())
                    await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

                // The game writes its preferences on the way out, so this has to come after.
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                RestoreSpawnPref(backup);
                ForgetRestore();
            }
            catch (Exception e)
            {
                ActivityLog.Write("while waiting to put the spawn-button setting back", e);
            }
        });
    }

    // ------------------------------------------------------------------ doing it

    public sealed record Result(bool Ok, string Message, string? LogFile = null, string? CommandLine = null);

    /// <summary>
    /// Starts the game, optionally straight into a named save.
    ///
    /// Refuses rather than guesses when the save is not there. The argument creates a brand new
    /// world when the name does not match anything - "[LoadSaveGame] Creating new save game" - and
    /// a mistyped name silently producing a fresh empty world on somebody else's PC is precisely
    /// the class of surprise this program exists to avoid.
    /// </summary>
    public static Result Start(GameLocation? location, string? world, string? saveName, string askedBy)
    {
        if (location is null)
            return new Result(false, "This PC has not worked out where the game is installed yet.");

        var installDir = GamePaths.ResolveInstall(location.InstallDir);
        if (installDir is null)
            return new Result(false, "The game does not appear to be installed on this PC.");

        if (GamePaths.IsGameRunning())
            return new Result(false, "The game is already running on this PC. Close it first.");

        if (!string.IsNullOrWhiteSpace(saveName))
        {
            var problem = WhyCannotLoad(location, world, saveName!);
            if (problem is not null) return new Result(false, problem);
        }

        var plan = PlanLaunch(location, installDir, world, saveName);

        if (!File.Exists(plan.Exe))
            return new Result(false, $"The game executable is not where it was expected ({plan.Exe}).");

        var steamNote = EnsureSteamRunning();

        // Captured before the launch, restored after the game finishes. Only when a save was
        // asked for: starting the game for somebody to play leaves their settings alone.
        var spawnPref = string.IsNullOrWhiteSpace(saveName) ? null : BackupSpawnPref();
        WriteDownRestore(spawnPref);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = plan.Exe,
                WorkingDirectory = installDir,
                UseShellExecute = false,
            };

            // ArgumentList, never a joined string: save names have spaces in them - "Chris Main
            // Save" - and quoting by hand is how -LoadSaveGame=Chris becomes a brand new world.
            foreach (var a in plan.Arguments) psi.ArgumentList.Add(a);

            using var started = Process.Start(psi);

            ActivityLog.Write(
                $"asked by {askedBy} to start the game"
                + (string.IsNullOrWhiteSpace(saveName) ? "" : $" in '{saveName}' (world '{world}')"));
            ActivityLog.Write($"  {plan.Basis}");
            ActivityLog.Write($"  {plan.CommandLine}");

            RestoreWhenTheGameFinishes(spawnPref);

            var what = string.IsNullOrWhiteSpace(saveName)
                ? "Starting the game."
                : $"Starting the game straight into '{saveName}', past the spawn screen.";

            return new Result(true, $"{what} {plan.Basis}.{steamNote} It takes a minute to load.",
                              plan.LogFile, plan.CommandLine);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                  or IOException or UnauthorizedAccessException)
        {
            ActivityLog.Write("could not start the game", e);
            RestoreSpawnPref(spawnPref);       // nothing started, so nothing to wait for
            ForgetRestore();
            return new Result(false, "Could not start it: " + e.Message);
        }
    }

    /// <summary>
    /// Why this save cannot be loaded, or null when it can.
    ///
    /// Phrased for somebody who is not going to read the code.
    /// </summary>
    public static string? WhyCannotLoad(GameLocation location, string? world, string saveName)
    {
        if (string.IsNullOrWhiteSpace(world))
            return $"'{saveName}' was asked for without saying which world it is in.";

        var dir = Path.Combine(location.SavesDir, world!, saveName);

        if (!Directory.Exists(dir))
            return $"There is no save called '{saveName}' in the world '{world}' on this PC."
                   + " Nothing was started, because asking for a save that is not there would have"
                   + " made a brand new empty one.";

        if (!File.Exists(Path.Combine(dir, "main.ttw")))
            return $"'{saveName}' is there but has no main.ttw, so the game would not load it.";

        return null;
    }

    /// <summary>
    /// Makes sure Steam is up, because the game needs it and will not wait.
    ///
    /// Best effort and never fatal: a PC where Steam cannot be found still gets its launch
    /// attempted, and the note explains what was and was not done.
    /// </summary>
    private static string EnsureSteamRunning()
    {
        try
        {
            var already = Process.GetProcessesByName("steam");
            try { if (already.Length > 0) return ""; }
            finally { foreach (var p in already) p.Dispose(); }

            var root = SteamLocator.FindSteamRoot();
            var steamExe = root is null ? null : Path.Combine(root, "steam.exe");
            if (steamExe is null || !File.Exists(steamExe))
                return " Steam does not appear to be running and could not be found, so this may not take.";

            Process.Start(new ProcessStartInfo { FileName = steamExe, UseShellExecute = true })?.Dispose();

            // Long enough for Steam to be answering, short enough that nobody thinks it hung.
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(500);
                var now = Process.GetProcessesByName("steam");
                try { if (now.Length > 0) { Thread.Sleep(3000); return " Steam was not running, so it was started first."; } }
                finally { foreach (var p in now) p.Dispose(); }
            }

            return " Steam was not running and was slow to start, so this may not take.";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                  or IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ watching it

    /// <summary>What the game is actually in, read back out of its own log.</summary>
    public sealed class LoadedSave
    {
        public string World { get; init; } = "";
        public string SaveName { get; init; } = "";

        /// <summary>The last thing the game said about loading a save, verbatim, when it said anything.</summary>
        public string? LoadNote { get; init; }

        public string Describe()
        {
            var where = string.IsNullOrWhiteSpace(SaveName)
                ? "no save loaded"
                : string.IsNullOrWhiteSpace(World) ? $"'{SaveName}'" : $"'{SaveName}' in {World}";
            return LoadNote is null ? where : $"{where}  ({LoadNote})";
        }
    }

    /// <summary>
    /// Which save the game last loaded on this PC.
    ///
    /// The game dumps every one of its preferences into the log each time it starts a world, so
    /// the last GamePref.GameName and GamePref.GameWorld in the newest log is what it is in now -
    /// no hooks, no injection, nothing near the running game.
    /// </summary>
    public static LoadedSave? ReadLoadedSave(GameLocation location)
    {
        try
        {
            var dir = Path.Combine(location.UserDataRoot, "logs");
            if (!Directory.Exists(dir)) return null;

            var newest = new DirectoryInfo(dir)
                .GetFiles("output_log*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) return null;

            string world = "", name = "", note = "";

            // Whole-file, but only for the two prefixes that matter, and a play session's log is
            // read once per request rather than continuously.
            using var reader = new StreamReader(
                new FileStream(newest.FullName, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete));

            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("GamePref.GameName ", StringComparison.Ordinal))
                    name = ValueAfterEquals(line);
                else if (line.StartsWith("GamePref.GameWorld ", StringComparison.Ordinal))
                    world = ValueAfterEquals(line);
                else if (line.Contains("[LoadSaveGame]", StringComparison.Ordinal))
                    note = line[line.IndexOf("[LoadSaveGame]", StringComparison.Ordinal)..].Trim();
            }

            if (name.Length == 0 && note.Length == 0) return null;

            return new LoadedSave
            {
                World = world,
                SaveName = name,
                LoadNote = note.Length == 0 ? null : note,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ValueAfterEquals(string line)
    {
        int eq = line.IndexOf('=');
        return eq < 0 ? "" : line[(eq + 1)..].Trim();
    }
}
