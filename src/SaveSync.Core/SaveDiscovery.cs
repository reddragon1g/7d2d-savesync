namespace SaveSync.Core;

/// <summary>One save on this machine: Saves\[World]\[SaveName].</summary>
public sealed class SaveSlot
{
    public required string World { get; init; }
    public required string SaveName { get; init; }
    public required string Folder { get; init; }

    /// <summary>Null when the tool has never registered this save. Not the same as "no save here".</summary>
    public Passport? Passport { get; set; }

    public WorldKind WorldKind { get; set; } = WorldKind.Unknown;

    /// <summary>Newest file mtime in the tree - a good proxy for "when was this last played".</summary>
    public DateTimeOffset LastWriteUtc { get; set; }

    public long SizeBytes { get; set; }
    public int FileCount { get; set; }

    public bool HasPassport => Passport is not null;

    /// <summary>Identity by name, used to pair up saves across machines before they share a SaveId.</summary>
    public string NameKey => World + "/" + SaveName;

    public string Display => $"{SaveName} ({World})";

    /// <summary>Generated worlds are unplayable on the far side without their GeneratedWorlds folder.</summary>
    public bool NeedsGeneratedWorld => WorldKind == WorldKind.Generated;
}

public static class SaveDiscovery
{
    /// <summary>
    /// A save folder is identified by the game's own world file. Checked because Saves\ also holds
    /// loose files (serveradmin.xml, newGameOptions.sdf) that are not saves.
    /// </summary>
    public static bool IsSaveDir(string dir)
        => Directory.Exists(dir)
           && (File.Exists(Path.Combine(dir, "main.ttw"))
               || File.Exists(Path.Combine(dir, "main.ttw.bak"))
               || Directory.Exists(Path.Combine(dir, "Region")));

    public static List<SaveSlot> Enumerate(GameLocation loc, bool measure = true, CancellationToken ct = default)
    {
        var result = new List<SaveSlot>();
        if (!Directory.Exists(loc.SavesDir)) return result;

        string[] worldDirs;
        try { worldDirs = Directory.GetDirectories(loc.SavesDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result; }

        foreach (var worldDir in worldDirs)
        {
            ct.ThrowIfCancellationRequested();
            var world = Path.GetFileName(worldDir);
            var kind = GamePaths.ClassifyWorld(loc, world);

            string[] saveDirs;
            try { saveDirs = Directory.GetDirectories(worldDir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var saveDir in saveDirs)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsSaveDir(saveDir)) continue;

                var slot = new SaveSlot
                {
                    World = world,
                    SaveName = Path.GetFileName(saveDir),
                    Folder = PathUtil.Normalize(saveDir),
                    Passport = Passport.Load(saveDir),
                    WorldKind = kind,
                };

                if (measure) Measure(slot, ct);
                result.Add(slot);
            }
        }

        return result
            .OrderByDescending(s => s.LastWriteUtc)
            .ThenBy(s => s.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static SaveSlot? Find(GameLocation loc, string world, string saveName)
    {
        var dir = Path.Combine(loc.SavesDir, world, saveName);
        if (!IsSaveDir(dir)) return null;

        var slot = new SaveSlot
        {
            World = world,
            SaveName = saveName,
            Folder = PathUtil.Normalize(dir),
            Passport = Passport.Load(dir),
            WorldKind = GamePaths.ClassifyWorld(loc, world),
        };
        Measure(slot);
        return slot;
    }

    /// <summary>Metadata-only walk: no hashing, so it stays fast enough for the list view.</summary>
    public static void Measure(SaveSlot slot, CancellationToken ct = default)
    {
        long size = 0;
        int count = 0;
        DateTimeOffset newest = DateTimeOffset.MinValue;

        foreach (var f in Manifest.EnumerateFiles(slot.Folder))
        {
            ct.ThrowIfCancellationRequested();

            // The passport is written by this tool, so its timestamp is always "just now" right
            // after a transfer. Counting it would make every freshly received save look played.
            if (Manifest.IsExcluded(Manifest.ToRelative(slot.Folder, f))) continue;

            try
            {
                var fi = new FileInfo(f);
                size += fi.Length;
                count++;
                var m = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
                if (m > newest) newest = m;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        slot.SizeBytes = size;
        slot.FileCount = count;
        slot.LastWriteUtc = newest == DateTimeOffset.MinValue ? DateTimeOffset.MinValue : newest;
    }

    /// <summary>
    /// How an incoming copy relates to this machine.
    ///
    /// Deliberately takes the slot rather than the passport: a save whose passport is missing is
    /// Unregistered, not NoLocal. Collapsing those two would make the first run on an
    /// already-populated machine silently overwrite real saves, which is the exact failure this
    /// whole tool exists to prevent.
    /// </summary>
    public static Relation Classify(SaveSlot? local, Passport incoming)
    {
        if (local is null) return Relation.NoLocal;
        if (local.Passport is null) return Relation.Unregistered;
        return Lineage.Compare(local.Passport, incoming);
    }

    /// <summary>
    /// Give an existing, previously unmanaged save a passport so it can take part in transfers.
    /// Called when the user confirms which copy is authoritative - never automatically, because
    /// minting independently on two machines produces two unrelated histories.
    /// </summary>
    public static Passport Adopt(SaveSlot slot, GameLocation loc, string gameVersion = "", string? identity = null)
    {
        var p = Passport.Mint(slot.World, slot.SaveName, identity ?? Machine.Identity, slot.WorldKind);
        p.MachineTag = Machine.Identity;
        p.GameVersion = gameVersion;
        p.LastPlayedAt = slot.LastWriteUtc == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : slot.LastWriteUtc;
        p.SizeBytes = slot.SizeBytes;
        p.FileCount = slot.FileCount;
        p.ContentMTimeTicks = slot.LastWriteUtc == DateTimeOffset.MinValue ? 0 : slot.LastWriteUtc.UtcTicks;
        p.Save(slot.Folder);
        slot.Passport = p;
        return p;
    }

    /// <summary>
    /// Reads the game version the save was written by, when the game recorded one nearby.
    /// Best effort: a missing value downgrades to a warning, never a block.
    /// </summary>
    public static string ReadGameVersionHint(GameLocation loc)
        => ReadVersionFromLogs(loc) is { Length: > 0 } fromLog ? fromLog : ReadVersionFromJoinedWorld(loc);

    /// <summary>
    /// The version the game itself printed at startup.
    ///
    /// This is the reliable source: every launch writes it, on every PC. The join-record method
    /// below only exists on a machine that has played multiplayer, so on a single-player PC the
    /// version came out blank - and a blank version silently disables the "that save was played on
    /// a newer game version" warning, which is the one thing standing between a 3.2 save and a
    /// 3.1 install.
    /// </summary>
    private static string ReadVersionFromLogs(GameLocation loc)
    {
        try
        {
            var logs = Path.Combine(loc.UserDataRoot, "logs");
            if (!Directory.Exists(logs)) return "";

            var newest = new DirectoryInfo(logs)
                .GetFiles("output_log*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) return "";

            // The banner is in the first few lines; a play session log runs to hundreds of MB.
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[64 * 1024];
            int read = stream.Read(buffer, 0, buffer.Length);
            var head = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

            var m = System.Text.RegularExpressions.Regex.Match(
                head, @"Version:\s*(V[ ]?[0-9][^\s,)]*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>
    /// Fallback: the client-side copy of a joined world records the host's version. Only present on
    /// a machine that has played multiplayer, which is why it is second rather than first.
    /// </summary>
    private static string ReadVersionFromJoinedWorld(GameLocation loc)
    {
        try
        {
            if (!Directory.Exists(loc.SavesLocalDir)) return "";
            foreach (var d in Directory.GetDirectories(loc.SavesLocalDir))
            {
                var xml = Path.Combine(d, "RemoteWorldInfo.xml");
                if (!File.Exists(xml)) continue;
                var text = File.ReadAllText(xml);
                var m = System.Text.RegularExpressions.Regex.Match(text, @"gameVersion\s*=\s*""([^""]+)""");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return "";
    }
}
