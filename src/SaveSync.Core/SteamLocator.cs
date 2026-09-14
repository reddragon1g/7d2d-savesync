using Microsoft.Win32;

namespace SaveSync.Core;

/// <summary>
/// Finds Steam and, through it, the game. Never assumes C:\Program Files (x86) - libraries live on
/// whatever drives the user added, and this machine alone has three.
/// </summary>
public static class SteamLocator
{
    public const string SevenDaysAppId = "251570";

    public static string? FindSteamRoot()
    {
        foreach (var probe in EnumerateSteamRootCandidates())
        {
            if (string.IsNullOrWhiteSpace(probe)) continue;
            var norm = PathUtil.Normalize(probe!);
            if (Directory.Exists(Path.Combine(norm, "steamapps"))) return norm;
        }
        return null;
    }

    private static IEnumerable<string?> EnumerateSteamRootCandidates()
    {
        // Steam writes SteamPath with forward slashes and a lowercase drive letter, which is why
        // everything here goes through PathUtil.Normalize before being compared or returned.
        yield return ReadRegistry(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        yield return ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        yield return ReadRegistry(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
    }

    /// <summary>Every Steam library on this machine, the default one included, normalized and deduped.</summary>
    public static List<string> LibraryFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            var norm = PathUtil.Normalize(p!);
            if (!Directory.Exists(norm)) return;
            if (seen.Add(norm)) result.Add(norm);
        }

        var root = FindSteamRoot();
        if (root is null) return result;
        Add(root);

        var node = Vdf.ParseFile(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
        if (node is null) return result;

        // Shape is { "libraryfolders" { "0" { "path" "..." } "1" { ... } } }; older Steam builds
        // omit the outer key.
        var container = node.Child("libraryfolders") ?? node;
        foreach (var child in container.Children.Values) Add(child.Value("path"));

        return result;
    }

    /// <summary>
    /// Install directory for an app, resolved through its appmanifest so a renamed or relocated
    /// folder still resolves.
    /// </summary>
    public static string? FindAppInstallDir(string appId)
    {
        foreach (var lib in LibraryFolders())
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            var node = Vdf.ParseFile(manifest);
            var installDir = node?.Child("AppState")?.Value("installdir") ?? node?.Value("installdir");
            if (string.IsNullOrWhiteSpace(installDir)) continue;

            var full = PathUtil.Normalize(Path.Combine(lib, "steamapps", "common", installDir!));
            if (Directory.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>
    /// Launch options the user set in Steam. This is where a -UserDataFolder redirect hides, and it
    /// is the most common reason saves are not where you would expect them.
    /// </summary>
    public static IEnumerable<string> LaunchOptions(string appId)
    {
        var root = FindSteamRoot();
        if (root is null) yield break;

        var userdata = Path.Combine(root, "userdata");
        if (!Directory.Exists(userdata)) yield break;

        string[] accounts;
        try { accounts = Directory.GetDirectories(userdata); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var acct in accounts)
        {
            var cfg = Path.Combine(acct, "config", "localconfig.vdf");
            var node = Vdf.ParseFile(cfg);
            var apps = node?.Child("UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
            var opts = apps?.Child(appId)?.Value("LaunchOptions");
            if (!string.IsNullOrWhiteSpace(opts)) yield return opts!;
        }
    }

    private static string? ReadRegistry(RegistryHive hive, string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
