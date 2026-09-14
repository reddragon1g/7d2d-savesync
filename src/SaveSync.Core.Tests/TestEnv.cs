using SaveSync.Core;

namespace SaveSync.Core.Tests;

/// <summary>
/// A disposable fake 7 Days to Die user-data folder.
///
/// Synthetic rather than real saves so the destructive cases - interrupted commits, corrupted
/// payloads, divergence - can be run hundreds of times without risking anything that matters.
/// The shape mirrors what the real game writes: Region chunk data, one .ttp per player, and the
/// paired .bak files the game keeps for its own crash recovery.
/// </summary>
public sealed class TestEnv : IDisposable
{
    public string Root { get; }
    public GameLocation Location { get; }
    public AppConfig Config { get; }

    public TestEnv()
    {
        Root = Path.Combine(Path.GetTempPath(), "savesync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        var userData = Path.Combine(Root, "userdata");
        Directory.CreateDirectory(Path.Combine(userData, "Saves"));
        Directory.CreateDirectory(Path.Combine(userData, "logs"));

        Location = new GameLocation
        {
            UserDataRoot = userData,
            InstallDir = null, // forces the built-in stock world list, keeping tests deterministic
            Provenance = "test",
        };

        // Pinned inside this test's own folder. Peering writes the config back to disk, and
        // without this a LAN test saved its loopback peer over the real user's settings.
        Config = new AppConfig
        {
            SnapshotsToKeep = 5,
            SourcePath = Path.Combine(Root, "config.json"),
        };

        // Tests must never depend on whether the real game happens to be open. Each test that
        // deliberately flips this switch restores it in a finally block.
        TransferEngine.IsGameRunningProbe = () => false;
    }

    public TransferEngine NewEngine() => new(Config, Location);

    /// <summary>Creates a save that looks like the real thing. Contents are deterministic from the seed.</summary>
    /// <summary>Navezgane's value on a real machine, so tests and reality agree on what a world is.</summary>
    public const uint DefaultWorldFingerprint = 2348195674;

    /// <summary>The two players every synthetic save has, exactly as the game records them.</summary>
    public static readonly string[] PlayerIds =
    {
        "aaaaaaaa111122223333444455556666",
        "bbbbbbbb777788889999aaaabbbbcccc",
    };

    public string MakeSave(
        string world = "Navezgane", string saveName = "My Game", int seed = 1, int regionFiles = 3,
        uint? worldFingerprint = null, long? gameTimeTicks = null)
    {
        var dir = Path.Combine(Location.SavesDir, world, saveName);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "Region"));
        Directory.CreateDirectory(Path.Combine(dir, "Player"));
        Directory.CreateDirectory(Path.Combine(dir, "ConfigsDump"));
        Directory.CreateDirectory(Path.Combine(dir, "DynamicMeshes"));

        var rnd = new Random(seed);

        // A real ttw header, not random bytes. The tool reads the game version, the world and the
        // clock straight out of this, so a fake one silently turns every test of that logic into a
        // test of the "could not read it" branch.
        WriteWorldFile(Path.Combine(dir, "main.ttw"), worldFingerprint ?? DefaultWorldFingerprint,
            gameTimeTicks ?? 24000L * 9, rnd);
        WriteBytes(Path.Combine(dir, "main.ttw.bak"), rnd, 4096);
        WriteBytes(Path.Combine(dir, "decoration.7dt"), rnd, 2048);
        // The real shape, listing the same two players whose .ttp files are written below - a
        // save that lists nobody cannot exercise anything that compares players.
        WritePlayersXml(Path.Combine(dir, "players.xml"), PlayerIds);

        foreach (var name in new[] { "power", "vehicles", "turrets", "drones", "blockLimits" })
        {
            WriteBytes(Path.Combine(dir, name + ".dat"), rnd, 256);
            WriteBytes(Path.Combine(dir, name + ".dat.bak"), rnd, 256);
        }

        for (int i = 0; i < regionFiles; i++)
            WriteBytes(Path.Combine(dir, "Region", $"r.{i}.0.7rg"), rnd, 8192);

        // Two players in one world, exactly as the real game stores them.
        foreach (var eos in PlayerIds.Select(id => "EOS_" + id))
        {
            WriteBytes(Path.Combine(dir, "Player", eos + ".ttp"), rnd, 1024);
            WriteBytes(Path.Combine(dir, "Player", eos + ".ttp.bak"), rnd, 1024);
            WriteText(Path.Combine(dir, "Player", eos + ".ttp.meta"), "meta");
        }

        WriteText(Path.Combine(dir, "ConfigsDump", "blocks.xml"), new string('x', 512));
        return dir;
    }

    /// <summary>
    /// Creates a mod folder the way the game expects one.
    ///
    /// <paramref name="wrapper"/> reproduces the commonest real-world mess: a zip extracted with an
    /// extra folder around the mod, so ModInfo.xml ends up a level deeper than it should be.
    /// <paramref name="settings"/> stands in for the config file a mod keeps inside its own folder,
    /// which is the thing that must never be written over on the receiving PC.
    /// </summary>
    public string MakeMod(
        string folderName,
        string version = "1.0.0",
        int seed = 7,
        string? wrapper = null,
        string? settings = null,
        string? displayName = null)
    {
        var modsRoot = Path.Combine(Location.UserDataRoot, "Mods");
        var dir = wrapper is null
            ? Path.Combine(modsRoot, folderName)
            : Path.Combine(modsRoot, wrapper, folderName);

        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "Config"));

        // Verbatim on purpose: this is the real ModInfo.xml shape, and it must stay readable.
        WriteText(Path.Combine(dir, "ModInfo.xml"), $@"<?xml version=""1.0"" encoding=""UTF-8"" ?>
<xml>
  <Name value=""{folderName}"" />
  <DisplayName value=""{displayName ?? folderName}"" />
  <Version value=""{version}"" />
  <Author value=""tests"" />
</xml>
");

        WriteText(Path.Combine(dir, "Config", "blocks.xml"), $"<blocks seed=\"{seed}\" />");
        WriteBytes(Path.Combine(dir, "Config", "icons.bin"), new Random(seed), 512);

        if (settings is not null)
            WriteText(Path.Combine(dir, "Config", "settings.xml"), settings);

        return dir;
    }

    /// <summary>Adds map data so a world counts as random-gen rather than stock.</summary>
    public string MakeGeneratedWorld(string world)
    {
        var dir = Path.Combine(Location.GeneratedWorldsDir, world);
        Directory.CreateDirectory(dir);
        WriteText(Path.Combine(dir, "map_info.xml"), $"<MapInfo name=\"{world}\" />");
        WriteBytes(Path.Combine(dir, "dtm.raw"), new Random(99), 4096);
        return dir;
    }

    /// <summary>Simulates a play session: mutates content and moves timestamps forward.</summary>
    public void Play(string saveDir, int seed)
    {
        var rnd = new Random(seed);
        WriteBytes(Path.Combine(saveDir, "main.ttw"), rnd, 4096);
        WriteBytes(Path.Combine(saveDir, "Region", "r.0.0.7rg"), rnd, 8192);
        WriteBytes(Path.Combine(saveDir, "Region", $"r.{seed}.9.7rg"), rnd, 8192);

        var now = DateTime.UtcNow.AddSeconds(5);
        foreach (var f in Manifest.EnumerateFiles(saveDir))
        {
            try { new FileInfo(f).LastWriteTimeUtc = now; } catch (IOException) { }
        }
    }

    public SaveSlot Slot(string world = "Navezgane", string saveName = "My Game")
        => SaveDiscovery.Find(Location, world, saveName)
           ?? throw new InvalidOperationException($"No save at {world}/{saveName}");

    /// <summary>Registers a save so it can take part in transfers, as the user confirming ownership would.</summary>
    public SaveSlot AdoptedSlot(string world = "Navezgane", string saveName = "My Game")
    {
        var slot = Slot(world, saveName);
        if (slot.Passport is null) SaveDiscovery.Adopt(slot, Location);
        return slot;
    }

    /// <summary>
    /// Writes a main.ttw the tool can actually read: the magic, the format number, a length-
    /// prefixed game version, then the value that identifies the world and the game clock at the
    /// offsets the real file puts them.
    /// </summary>
    public static void WriteWorldFile(string path, uint worldFingerprint, long ticks, Random rnd)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write(new[] { (byte)'t', (byte)'t', (byte)'w', (byte)0 });
        w.Write(SaveEvidence.KnownFormat);

        var version = "V 3.2.0 (b10)";
        w.Write((byte)version.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes(version));

        // Filler up to the two fields that matter, then the fields themselves.
        for (int i = 0; i < 13; i++) w.Write(i);
        w.Write(worldFingerprint);
        w.Write((uint)ticks);

        var tail = new byte[4096];
        rnd.NextBytes(tail);
        w.Write(tail);

        File.WriteAllBytes(path, ms.ToArray());
    }

    public static void WritePlayersXml(string path, IEnumerable<string> playerIds)
    {
        var rows = string.Join(Environment.NewLine, playerIds.Select((id, i) =>
            $"  <player platform=\"EOS\" userid=\"{id}\" nativeplatform=\"Steam\" "
            + $"nativeuserid=\"7656119900244017{i}\" playername=\"Tester{i}\" playgroup=\"Standalone\" "
            + $"lastlogin=\"2026-09-13 21:29:2{i}\" position=\"114{i},80,87{i}\" />"));

        WriteText(path,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + Environment.NewLine
            + "<persistentplayerdata version=\"1\">" + Environment.NewLine
            + rows + Environment.NewLine
            + "</persistentplayerdata>" + Environment.NewLine);
    }

    public static void WriteBytes(string path, Random rnd, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var buf = new byte[length];
        rnd.NextBytes(buf);
        File.WriteAllBytes(path, buf);
    }

    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>Flips a byte in the middle of a file, the way a bad stick or cable would.</summary>
    public static void CorruptFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) { File.WriteAllBytes(path, new byte[] { 1 }); return; }
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose()
    {
        try { PathUtil.DeleteTree(Root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
