namespace SaveSync.Core;

/// <summary>
/// The travelling identity of a save. Written as .savesync.json inside the save folder and into
/// every transfer package.
///
/// This exists because the machines usually cannot see each other when a decision has to be made
/// (the laptop travels). Every copy therefore has to carry enough history to be judged on its own.
/// </summary>
public sealed class Passport
{
    public const string FileName = ".savesync.json";
    public const int CurrentSchema = 1;

    /// <summary>Bumped only for breaking shape changes. Readers refuse anything newer than they know.</summary>
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Stable identity of this save slot across every machine. Minted once, never changes.</summary>
    public string SaveId { get; set; } = "";

    public string World { get; set; } = "";
    public string SaveName { get; set; } = "";

    /// <summary>Unique id for THIS commit. Ancestry is computed from these, never from Ordinal.</summary>
    public string VersionId { get; set; } = "";

    /// <summary>Ancestors, newest first, excluding VersionId. Capped at Lineage.MaxChain.</summary>
    public List<string> Chain { get; set; } = new();

    /// <summary>Display counter only. Not reliable for ordering once more than two machines exist.</summary>
    public long Ordinal { get; set; }

    /// <summary>
    /// Who produced this version - a person's name once profiles are in use. This is what the
    /// UI says, because nobody knows or cares what their PC is called.
    /// </summary>
    public string LastPlayedOn { get; set; } = "";

    /// <summary>MACHINE\user, kept for diagnostics only. Never shown as the main identity.</summary>
    public string MachineTag { get; set; } = "";

    public DateTimeOffset LastPlayedAt { get; set; }
    public DateTimeOffset CommittedAt { get; set; }

    public long SizeBytes { get; set; }
    public int FileCount { get; set; }

    /// <summary>
    /// Newest last-written time across the save's actual contents, excluding this passport file.
    ///
    /// This is how "has it been played since we last copied it" is decided, and it deliberately
    /// compares a file time against a file time. Comparing against CommittedAt instead is wrong
    /// twice over: after an import CommittedAt belongs to the machine that sent it, and the two
    /// PCs do not necessarily agree on what time it is.
    /// </summary>
    public long ContentMTimeTicks { get; set; }

    /// <summary>SHA-256 over the file manifest. Detects any byte-level difference in the payload.</summary>
    public string ManifestSha { get; set; } = "";

    /// <summary>e.g. V.3.20.10 - a mismatch is warned about, never silently applied.</summary>
    public string GameVersion { get; set; } = "";

    /// <summary>Generated worlds must carry their GeneratedWorlds folder with them or the save will not load.</summary>
    public WorldKind WorldKind { get; set; } = WorldKind.Unknown;

    /// <summary>Set on a package produced by Export. Lets the importer name the origin in plain English.</summary>
    public string PackagedBy { get; set; } = "";

    public Passport Clone() => Json.Read<Passport>(Json.Write(this))!;

    /// <summary>
    /// The passport for a new commit descending from this one. The current version becomes the head
    /// of the chain, so on the far side ancestry is a simple containment test.
    /// </summary>
    public Passport NewChild(string playedOn, DateTimeOffset lastPlayedAt)
    {
        var child = Clone();
        child.Chain = Lineage.PushAncestor(Chain, VersionId);
        child.VersionId = Ids.NewVersionId();
        child.Ordinal = Ordinal + 1;
        child.LastPlayedOn = playedOn;
        child.LastPlayedAt = lastPlayedAt;
        child.CommittedAt = DateTimeOffset.UtcNow;
        return child;
    }

    public static Passport Mint(string world, string saveName, string playedOn, WorldKind kind)
        => new()
        {
            SaveId = Ids.NewSaveId(),
            World = world,
            SaveName = saveName,
            VersionId = Ids.NewVersionId(),
            Chain = new List<string>(),
            Ordinal = 1,
            LastPlayedOn = playedOn,
            LastPlayedAt = DateTimeOffset.UtcNow,
            CommittedAt = DateTimeOffset.UtcNow,
            WorldKind = kind,
        };

    public static Passport? Load(string saveDir)
    {
        var p = Json.ReadFile<Passport>(Path.Combine(saveDir, FileName));
        if (p is null) return null;
        if (p.Schema > CurrentSchema) return null;
        if (string.IsNullOrWhiteSpace(p.SaveId) || string.IsNullOrWhiteSpace(p.VersionId)) return null;
        return p;
    }

    public void Save(string saveDir) => Json.WriteFileAtomic(Path.Combine(saveDir, FileName), this);

    public string Describe() => $"{SaveName} ({World}) v{Ordinal} from {LastPlayedOn}";
}

public enum WorldKind
{
    Unknown = 0,
    /// <summary>Ships with the game. Present on every install, nothing extra to copy.</summary>
    Stock = 1,
    /// <summary>Random-gen. The GeneratedWorlds folder must travel with the save.</summary>
    Generated = 2,
}

public static class Ids
{
    public static string NewSaveId() => Guid.NewGuid().ToString("N");

    /// <summary>64 bits of randomness. Needs no coordination between machines, which is the point.</summary>
    public static string NewVersionId() => Guid.NewGuid().ToString("N").Substring(0, 16);
}
