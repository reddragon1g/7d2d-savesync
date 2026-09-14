namespace SaveSync.Core;

/// <summary>
/// What is heavy inside a save, by the only measure available from outside the game: bytes.
///
/// "The base is too complex" and "it started when we wired up the lights" are both claims about a
/// save's contents, and until now nothing here could check either. The game writes its world into
/// separate files by subject - the electrical grid into power.dat, player-built structures into
/// DynamicMeshes, the terrain into Region - so their sizes say, roughly but honestly, where the
/// weight of a world actually sits.
///
/// Rough on purpose. A byte count is not an object count and this does not pretend otherwise; what
/// it is good for is comparison. The same save measured on two machines, or one save measured
/// against another, turns an argument about what is making a game slow into a number.
/// </summary>
public sealed class SaveWeight
{
    public string World { get; init; } = "";
    public string SaveName { get; init; } = "";

    /// <summary>The electrical grid: every wire, switch, light, trigger and relay in the world.</summary>
    public long PowerBytes { get; init; }

    /// <summary>Player-built structures, held as separate meshes so they can be damaged.</summary>
    public long DynamicMeshBytes { get; init; }
    public int DynamicMeshFiles { get; init; }

    /// <summary>The terrain, and everything built into it.</summary>
    public long RegionBytes { get; init; }
    public int RegionFiles { get; init; }

    public long PlayerBytes { get; init; }
    public int PlayerFiles { get; init; }

    public long TotalBytes { get; init; }

    public static SaveWeight? Read(GameLocation location, string world, string saveName)
    {
        try
        {
            var dir = Path.Combine(location.SavesDir, world, saveName);
            if (!Directory.Exists(dir)) return null;

            var (meshBytes, meshFiles) = Folder(Path.Combine(dir, "DynamicMeshes"));
            var (regionBytes, regionFiles) = Folder(Path.Combine(dir, "Region"));
            var (playerBytes, playerFiles) = Folder(Path.Combine(dir, "Player"));

            long power = 0;
            try
            {
                var p = Path.Combine(dir, "power.dat");
                if (File.Exists(p)) power = new FileInfo(p).Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            return new SaveWeight
            {
                World = world,
                SaveName = saveName,
                PowerBytes = power,
                DynamicMeshBytes = meshBytes,
                DynamicMeshFiles = meshFiles,
                RegionBytes = regionBytes,
                RegionFiles = regionFiles,
                PlayerBytes = playerBytes,
                PlayerFiles = playerFiles,
                TotalBytes = Folder(dir, recurse: true).Bytes,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (long Bytes, int Files) Folder(string path, bool recurse = false)
    {
        try
        {
            if (!Directory.Exists(path)) return (0, 0);

            var files = Directory.GetFiles(path, "*",
                recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

            long total = 0;
            foreach (var f in files)
            {
                try { total += new FileInfo(f).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }

            return (total, files.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    public string Describe()
    {
        var lines = new List<string>
        {
            $"'{SaveName}' in {World} - {PathUtil.HumanBytes(TotalBytes)} in total:",
            $"  electrical grid   {PathUtil.HumanBytes(PowerBytes),10}   (power.dat)",
            $"  base structures   {PathUtil.HumanBytes(DynamicMeshBytes),10}   ({DynamicMeshFiles} files)",
            $"  terrain           {PathUtil.HumanBytes(RegionBytes),10}   ({RegionFiles} region files)",
            $"  players           {PathUtil.HumanBytes(PlayerBytes),10}   ({PlayerFiles} files)",
        };

        // A yardstick, because a number with nothing to compare it against settles no arguments.
        // A world with a few switches and a garage door runs to a handful of kilobytes.
        if (PowerBytes > 512 * 1024)
            lines.Add($"  >> The electrical grid is LARGE. {PathUtil.HumanBytes(PowerBytes)} of wiring "
                      + "is a serious amount of it, and the game re-evaluates the whole grid every "
                      + "0.16 seconds on the machine hosting the world.");
        else if (PowerBytes > 64 * 1024)
            lines.Add($"  >> The electrical grid is substantial at {PathUtil.HumanBytes(PowerBytes)}.");

        return string.Join(Environment.NewLine, lines);
    }
}
