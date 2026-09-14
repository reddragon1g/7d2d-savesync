using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SaveSync.Core;

/// <summary>A resolved 7 Days to Die installation: where saves live, and where the game is installed.</summary>
public sealed class GameLocation
{
    public required string UserDataRoot { get; init; }
    public string? InstallDir { get; init; }

    /// <summary>How we found it, surfaced in the UI so the user can sanity-check our guess.</summary>
    public required string Provenance { get; init; }

    public string SavesDir => Path.Combine(UserDataRoot, "Saves");
    public string SavesLocalDir => Path.Combine(UserDataRoot, "SavesLocal");
    public string GeneratedWorldsDir => Path.Combine(UserDataRoot, "GeneratedWorlds");

    public override string ToString() => UserDataRoot;
}

/// <summary>
/// Locates the game without assuming anything. Saves default to %APPDATA%\7DaysToDie but a
/// -UserDataFolder launch option moves them, and the install can sit in any Steam library on any
/// drive. Every guess here is validated before use and every guess is overridable from the UI.
/// </summary>
public static class GamePaths
{
    public const string ProcessName = "7DaysToDie";

    /// <summary>
    /// Worlds shipped with the game, used only when the install cannot be located. The
    /// authoritative list is Data\Worlds inside the install.
    /// </summary>
    public static readonly string[] BuiltInStockWorlds =
    {
        "Empty", "Navezgane", "Playtesting",
        "Pregen06k01", "Pregen06k02", "Pregen08k01", "Pregen08k02",
    };

    private static readonly Regex UserDataFolderRx = new(
        @"-UserDataFolder\s*=\s*(?:""(?<q>[^""]+)""|(?<u>[^\s""]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record Candidate(string Path, string Provenance, bool Valid);

    /// <summary>Best guess, or null when nothing plausible exists and the user must browse to it.</summary>
    public static GameLocation? Discover(string? userDataOverride = null, string? installOverride = null)
    {
        var install = ResolveInstall(installOverride);

        if (!string.IsNullOrWhiteSpace(userDataOverride))
        {
            return LooksLikeUserData(userDataOverride!)
                ? new GameLocation { UserDataRoot = userDataOverride!, InstallDir = install, Provenance = "chosen by you" }
                : null;
        }

        var best = UserDataCandidates().FirstOrDefault(c => c.Valid);
        if (best is null) return null;

        return new GameLocation { UserDataRoot = best.Path, InstallDir = install, Provenance = best.Provenance };
    }

    /// <summary>Every place the save folder might be, best first, each marked valid or not.</summary>
    public static List<Candidate> UserDataCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Candidate>();

        void Add(string? path, string why)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path!.Trim()); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (!seen.Add(full)) return;
            result.Add(new Candidate(full, why, LooksLikeUserData(full)));
        }

        // A -UserDataFolder launch option wins. If one is set, the default folder is stale, and
        // syncing that instead would quietly move the wrong save.
        foreach (var opts in SteamLocator.LaunchOptions(SteamLocator.SevenDaysAppId))
        {
            var redirected = ParseUserDataFolderArg(opts);
            if (redirected is not null) Add(redirected, "Steam launch options (-UserDataFolder)");
        }

        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "7DaysToDie"),
            "default location (AppData\\Roaming)");

        // Some people relocate the whole user folder next to the install.
        var install = ResolveInstall(null);
        if (install is not null)
        {
            Add(Path.Combine(install, "7DaysToDie"), "beside the game install");
            var parent = Path.GetDirectoryName(install);
            if (parent is not null) Add(Path.Combine(parent, "7DaysToDie"), "beside the game install");
        }

        return result;
    }

    /// <summary>Pulls the path out of -UserDataFolder=..., quoted or bare.</summary>
    public static string? ParseUserDataFolderArg(string launchOptions)
    {
        if (string.IsNullOrWhiteSpace(launchOptions)) return null;
        var m = UserDataFolderRx.Match(launchOptions);
        if (!m.Success) return null;
        var v = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    /// <summary>
    /// A folder is the user data root when it carries any of the fingerprints the game leaves.
    /// Deliberately permissive about which one, because a fresh install has no Saves folder yet.
    /// </summary>
    public static bool LooksLikeUserData(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        string[] marks = { "Saves", "SavesLocal", "GeneratedWorlds", "logs" };
        foreach (var m in marks)
            if (Directory.Exists(Path.Combine(path, m))) return true;
        return File.Exists(Path.Combine(path, "launchersettings.json"));
    }

    public static string? ResolveInstall(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && IsInstallDir(overridePath!)) return overridePath;

        var running = RunningGameExePath();
        if (running is not null)
        {
            var dir = Path.GetDirectoryName(running);
            if (dir is not null && IsInstallDir(dir)) return dir;
        }

        var steam = SteamLocator.FindAppInstallDir(SteamLocator.SevenDaysAppId);
        if (steam is not null && IsInstallDir(steam)) return steam;

        foreach (var p in UninstallKeyPaths())
            if (IsInstallDir(p)) return p;

        return null;
    }

    public static bool IsInstallDir(string path)
        => !string.IsNullOrWhiteSpace(path)
           && Directory.Exists(path)
           && File.Exists(Path.Combine(path, ProcessName + ".exe"));

    /// <summary>Worlds this install ships with; falls back to the built-in list if the install is unknown.</summary>
    public static IReadOnlyCollection<string> StockWorlds(string? installDir)
    {
        if (installDir is not null)
        {
            var worlds = Path.Combine(installDir, "Data", "Worlds");
            if (Directory.Exists(worlds))
            {
                try
                {
                    var names = Directory.GetDirectories(worlds).Select(Path.GetFileName).OfType<string>().ToArray();
                    if (names.Length > 0) return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
                }
                catch (IOException) { /* fall through to the built-in list */ }
                catch (UnauthorizedAccessException) { /* fall through to the built-in list */ }
            }
        }
        return new HashSet<string>(BuiltInStockWorlds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Is this world random-gen? The presence of GeneratedWorlds\[world] is the primary test,
    /// because it holds regardless of whether the install was located at all.
    /// </summary>
    public static WorldKind ClassifyWorld(GameLocation loc, string world)
    {
        if (Directory.Exists(Path.Combine(loc.GeneratedWorldsDir, world))) return WorldKind.Generated;
        if (StockWorlds(loc.InstallDir).Contains(world)) return WorldKind.Stock;
        return WorldKind.Unknown;
    }

    public static bool IsGameRunning()
    {
        var procs = Process.GetProcessesByName(ProcessName);
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    public static string? RunningGameExePath()
    {
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            try { return p.MainModule?.FileName; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Access denied across bitness, or it exited mid-query. Try the next one.
            }
            finally { p.Dispose(); }
        }
        return null;
    }

    /// <summary>Non-Steam and relocated installs still leave an uninstall entry behind.</summary>
    private static IEnumerable<string> UninstallKeyPaths()
    {
        string[] roots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var root in roots)
        {
            string[] subNames;
            RegistryKey? baseKey = null;
            RegistryKey? key = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                key = baseKey.OpenSubKey(root);
                if (key is null) continue;
                subNames = key.GetSubKeyNames();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }
            finally
            {
                // key stays open below; only dispose what we are done with here
            }

            foreach (var sub in subNames)
            {
                string? loc = null;
                try
                {
                    using var s = key.OpenSubKey(sub);
                    var name = s?.GetValue("DisplayName") as string;
                    if (name is null || name.IndexOf("7 Days", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    loc = s?.GetValue("InstallLocation") as string;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(loc)) yield return loc!;
            }

            key.Dispose();
            baseKey?.Dispose();
        }
    }
}
