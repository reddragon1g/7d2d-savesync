namespace SaveSync.Core;

/// <summary>
/// Where the tool keeps its own working data.
///
/// Snapshots and staging deliberately live inside the game's user-data root rather than in
/// LOCALAPPDATA. Commit and rollback are directory moves, and a move is only instant and atomic
/// within one volume. If the user redirects saves to another drive with -UserDataFolder, a
/// LOCALAPPDATA workspace would silently downgrade every commit into a multi-gigabyte copy and
/// lose the atomicity the whole design rests on.
/// </summary>
public sealed class Workspace
{
    public const string DirName = ".savesync";

    public Workspace(string userDataRoot)
    {
        UserDataRoot = PathUtil.Normalize(userDataRoot);
        Root = Path.Combine(UserDataRoot, DirName);
    }

    public string UserDataRoot { get; }
    public string Root { get; }

    /// <summary>Previous versions, kept before every overwrite. Never pruned below the last-known-good.</summary>
    public string Snapshots => Path.Combine(Root, "snapshots");

    /// <summary>Half-written incoming payloads. Anything here is by definition not yet trusted.</summary>
    public string Staging => Path.Combine(Root, "staging");

    /// <summary>Where a replaced save is parked during the commit swap.</summary>
    public string Trash => Path.Combine(Root, "trash");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>
    /// Mod folders that were replaced, kept out of the Mods directory on purpose: a spare copy
    /// sitting beside the real one still has a ModInfo.xml, so the game would load the mod twice.
    /// </summary>
    public string ModBackups => Path.Combine(Root, "mod-backups");

    /// <summary>Packages received over the network, before this machine has decided anything.</summary>
    public string Inbox => Path.Combine(Root, "inbox");

    public string SnapshotsFor(string saveId) => Path.Combine(Snapshots, PathUtil.Sanitize(saveId));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Snapshots);
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Trash);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Inbox);
        Directory.CreateDirectory(ModBackups);

        // Keep it out of the way; the game never looks here but the user might.
        try
        {
            var di = new DirectoryInfo(Root);
            if ((di.Attributes & FileAttributes.Hidden) == 0) di.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Clears staging and trash left behind by a crash or a kill mid-transfer.</summary>
    public void CleanupOrphans()
    {
        foreach (var dir in new[] { Staging, Trash })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var d in Directory.GetDirectories(dir))
            {
                try { PathUtil.DeleteTree(d); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}

/// <summary>Per-machine settings, kept outside the game folder so a game reinstall cannot wipe them.</summary>
public static class AppFolders
{
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaveSync");

    public const string ConfigFileName = "config.json";

    /// <summary>Beside the program - the stick, the Desktop, Downloads, wherever it was put.</summary>
    public static string PortableConfigPath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

    /// <summary>Always writable by this user, on every machine. The fallback that cannot fail.</summary>
    public static string LocalConfigPath => Path.Combine(ConfigDir, ConfigFileName);

    /// <summary>
    /// Portable mode: a config.json sitting beside the EXE wins, so the copy that rides on the USB
    /// stick carries its own settings and does not inherit whatever machine it is plugged into.
    ///
    /// When both exist the newer one wins. That case is not exotic - it is what happens the moment
    /// the program runs from somewhere it cannot write back to (a write-protected stick, Program
    /// Files, a network share): settings go to the local copy from then on, and preferring the
    /// beside-EXE file regardless would mean reading stale settings forever with nothing saying why.
    /// </summary>
    public static string ConfigPath
    {
        get
        {
            var beside = PortableConfigPath;
            var local = LocalConfigPath;

            return Choose(beside, local);
        }
    }

    /// <summary>Split out from <see cref="ConfigPath"/> so the rule can be tested on real files.</summary>
    public static string Choose(string beside, string local)
    {
        if (!File.Exists(beside)) return local;
        if (!File.Exists(local)) return beside;

        try
        {
            return File.GetLastWriteTimeUtc(local) > File.GetLastWriteTimeUtc(beside) ? local : beside;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return beside;
        }
    }

    public static bool IsPortable => PathUtil.SamePath(Path.GetDirectoryName(ConfigPath) ?? "", AppContext.BaseDirectory);

    /// <summary>
    /// Whether the program can write next to itself. Answers the question every "it works on my
    /// PC" bug starts with, and decides where settings actually go.
    /// </summary>
    public static bool CanWriteBesideProgram()
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, ".savesync-write-test.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
