namespace SaveSync.Core;

/// <summary>
/// On-disk layout of a transfer package. Identical whether it travels on a USB stick or over the
/// LAN, so both paths run the same verify-then-commit code and only one of them can be subtly wrong.
/// </summary>
public static class PackageLayout
{
    public const string RootFolderName = "7DTD-SaveSync";
    public const string InfoFile = "package.json";
    public const string ManifestFile = "manifest.json";
    public const string PayloadDir = "payload";
    public const string WorldDir = "world";
    public const string ModsDir = "mods";
    public const string ModsListFile = "mods.json";
    public const string ModsManifestFile = "mods-manifest.json";

    public static string Info(string pkg) => Path.Combine(pkg, InfoFile);
    public static string Manifest(string pkg) => Path.Combine(pkg, ManifestFile);
    public static string Payload(string pkg) => Path.Combine(pkg, PayloadDir);
    public static string World(string pkg) => Path.Combine(pkg, WorldDir);
    public static string Mods(string pkg) => Path.Combine(pkg, ModsDir);
    public static string ModsList(string pkg) => Path.Combine(pkg, ModsListFile);
    public static string ModsManifest(string pkg) => Path.Combine(pkg, ModsManifestFile);

    public static bool IsPackage(string dir)
        => Directory.Exists(dir) && File.Exists(Info(dir)) && Directory.Exists(Payload(dir));
}

public sealed class PackageInfo
{
    public int Schema { get; set; } = 1;

    public Passport Passport { get; set; } = new();

    /// <summary>True when the package carries GeneratedWorlds data the destination may not have.</summary>
    public bool IncludesGeneratedWorld { get; set; }

    /// <summary>
    /// The mods that were installed on the sending PC when this save was packaged.
    ///
    /// Recorded even when the mod files themselves were not carried, because a save played with
    /// mods behaves badly or refuses to load without them, and naming what is missing is far more
    /// use than a save that silently misbehaves.
    /// </summary>
    public List<ModEntry> Mods { get; set; } = new();

    /// <summary>True when the mod FOLDERS travel in this package, not just their names.</summary>
    public bool IncludesModFiles { get; set; }

    public long ModBytes { get; set; }

    public string CreatedBy { get; set; } = "";
    public string CreatedOnMachineId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string ToolVersion { get; set; } = "";
    public string GameVersion { get; set; } = "";

    public long PayloadBytes { get; set; }
    public int PayloadFiles { get; set; }

    public static PackageInfo? Load(string packageDir) => Json.ReadFile<PackageInfo>(PackageLayout.Info(packageDir));

    public void Save(string packageDir) => Json.WriteFileAtomic(PackageLayout.Info(packageDir), this);
}

/// <summary>
/// Local record of packages already installed on this machine.
///
/// Kept here rather than only on the stick, because the stick may be write-protected, and because
/// a stale package sitting on a stick for three weeks is exactly how somebody reinstalls an old
/// save over a newer one.
/// </summary>
public sealed class ConsumedLedger
{
    public const string FileName = "consumed.json";

    public Dictionary<string, DateTimeOffset> Installed { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string PathFor(Workspace ws) => Path.Combine(ws.Root, FileName);

    public static ConsumedLedger Load(Workspace ws)
        => Json.ReadFile<ConsumedLedger>(PathFor(ws)) ?? new ConsumedLedger();

    public void Save(Workspace ws) => Json.WriteFileAtomic(PathFor(ws), this);

    private static string Key(string saveId, string versionId) => saveId + ":" + versionId;

    public bool WasInstalled(string saveId, string versionId) => Installed.ContainsKey(Key(saveId, versionId));

    public DateTimeOffset? WhenInstalled(string saveId, string versionId)
        => Installed.TryGetValue(Key(saveId, versionId), out var d) ? d : null;

    public void Record(string saveId, string versionId)
        => Installed[Key(saveId, versionId)] = DateTimeOffset.UtcNow;
}
