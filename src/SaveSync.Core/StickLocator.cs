namespace SaveSync.Core;

public sealed record StickCandidate(string Root, string Label, long FreeBytes, bool HasSaves, string Why);

/// <summary>
/// Works out which drive is "the stick".
///
/// The intended way to use this tool is to copy the EXE onto a USB stick and run it from there, so
/// the drive the program is sitting on is the first and best answer. Everything after that is a
/// fallback for people who copied it to their desktop instead.
/// </summary>
public static class StickLocator
{
    /// <summary>The drive this program is running from, when that drive is removable.</summary>
    public static string? RunningFromRemovable()
    {
        try
        {
            var root = Path.GetPathRoot(PathUtil.Normalize(AppContext.BaseDirectory));
            if (string.IsNullOrEmpty(root)) return null;

            var drive = new DriveInfo(root);
            return drive.IsReady && drive.DriveType == DriveType.Removable ? drive.RootDirectory.FullName : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when the program is running from the stick, which is the intended setup.</summary>
    public static bool IsRunningFromStick() => RunningFromRemovable() is not null;

    /// <summary>
    /// Every drive that could plausibly be the stick, best first. Fixed drives are included
    /// because plenty of external drives report themselves as fixed, and refusing to see one
    /// because Windows labelled it oddly would be a poor way to fail.
    /// </summary>
    public static List<StickCandidate> Candidates()
    {
        var systemRoot = PathUtil.Normalize(Path.GetPathRoot(Environment.SystemDirectory) ?? "");
        var running = RunningFromRemovable();
        var list = new List<StickCandidate>();

        foreach (var d in SafeDrives())
        {
            var root = d.RootDirectory.FullName;
            if (PathUtil.SamePath(root, systemRoot)) continue;

            bool hasSaves = Directory.Exists(Path.Combine(root, PackageLayout.RootFolderName));
            string label;
            try { label = d.VolumeLabel; } catch (IOException) { label = ""; }

            string why =
                running is not null && PathUtil.SamePath(root, running) ? "this program is running from here"
                : hasSaves ? "already has saves on it"
                : d.DriveType == DriveType.Removable ? "a removable drive"
                : "another drive";

            list.Add(new StickCandidate(root, string.IsNullOrWhiteSpace(label) ? "USB drive" : label,
                SafeFree(d), hasSaves, why));
        }

        return list
            .OrderByDescending(c => running is not null && PathUtil.SamePath(c.Root, running))
            .ThenByDescending(c => c.HasSaves)
            .ThenByDescending(c => c.Why == "a removable drive")
            .ToList();
    }

    /// <summary>Best single guess, or null when the user has to be asked.</summary>
    public static string? FindStick()
    {
        var running = RunningFromRemovable();
        if (running is not null) return running;

        var candidates = Candidates();
        var withSaves = candidates.FirstOrDefault(c => c.HasSaves);
        if (withSaves is not null) return withSaves.Root;

        var removable = candidates.Where(c => c.Why == "a removable drive").ToList();
        return removable.Count == 1 ? removable[0].Root : null;
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); }
        catch (IOException) { yield break; }

        foreach (var d in all)
        {
            bool ok;
            try { ok = d.IsReady && d.DriveType is DriveType.Removable or DriveType.Fixed; }
            catch (IOException) { ok = false; }
            if (ok) yield return d;
        }
    }

    private static long SafeFree(DriveInfo d)
    {
        try { return d.AvailableFreeSpace; }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
    }
}
