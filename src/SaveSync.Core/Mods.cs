using System.Xml.Linq;

namespace SaveSync.Core;

/// <summary>
/// Where a mod folder lives. The game loads from both and they are not interchangeable: a mod
/// installed under the game folder is wiped by a Steam file verify, one under the user-data folder
/// is not. A mod is therefore always put back into the same kind of place it came from.
/// </summary>
public enum ModRoot
{
    /// <summary>%APPDATA%\7DaysToDie\Mods - survives game updates and needs no admin rights.</summary>
    UserData,

    /// <summary>&lt;install&gt;\Mods - the classic location.</summary>
    Install,
}

/// <summary>One mod folder, as found on disk or as carried in a package.</summary>
public sealed class ModEntry
{
    /// <summary>The folder name. This, not the display name, is what the game keys on.</summary>
    public string FolderName { get; set; } = "";

    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";

    public ModRoot Root { get; set; } = ModRoot.UserData;

    public long SizeBytes { get; set; }
    public int FileCount { get; set; }

    /// <summary>
    /// Hash over this mod's file manifest. Two mods are "the same" only when this matches, which
    /// covers the case that matters most: same name, same version number, edited settings inside.
    /// </summary>
    public string ContentSha { get; set; } = "";

    /// <summary>Absolute path on this machine. Local only - never travels in a package.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Folder { get; set; } = "";

    /// <summary>
    /// How far below the Mods folder this was found. 1 is where a mod belongs; anything more means
    /// it was extracted with a wrapper folder around it. Copying it to the other PC quietly puts it
    /// at depth 1, which is the layout the game actually wants.
    /// </summary>
    public int NestedDepth { get; set; } = 1;

    /// <summary>True when this mod is buried deeper than the game expects on THIS machine.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAwkwardlyNested => NestedDepth > 1;

    /// <summary>
    /// Came with the game rather than with a person. These are never carried between PCs: the
    /// other machine already has its own copy from its own install, and that copy matches ITS game
    /// version. Overwriting it with a different version is a way to break a working game.
    /// </summary>
    public bool ShippedWithGame { get; set; }

    public string Label => FirstNonEmpty(DisplayName, Name, FolderName);

    public string Describe()
        => string.IsNullOrWhiteSpace(Version) ? Label : $"{Label} {Version}";

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "";
    }
}

/// <summary>What will happen to one mod when a package is applied.</summary>
public enum ModAction
{
    /// <summary>Not on this PC. It will be added, settings and all.</summary>
    Install,

    /// <summary>Already here, byte for byte. Nothing to do.</summary>
    Identical,

    /// <summary>
    /// Here, but not the same - a different version, or the same version with different settings.
    /// Left exactly as it is. This is the default and it is never overridden automatically.
    /// </summary>
    KeepExisting,
}

public sealed class ModPlanItem
{
    public required ModEntry Incoming { get; init; }
    public ModEntry? Local { get; init; }
    public required ModAction Action { get; init; }

    /// <summary>Set only when the user has explicitly asked to replace this one.</summary>
    public bool ReplaceApproved { get; set; }

    public string Explain() => Action switch
    {
        ModAction.Install => $"{Incoming.Describe()} will be added.",
        ModAction.Identical => $"{Incoming.Describe()} is already here and identical.",
        ModAction.KeepExisting => Local is null
            ? $"{Incoming.Describe()} is already here."
            : $"{Incoming.Describe()} is already here as {Local.Describe()}, with its own settings. "
              + "Yours is being kept.",
        _ => Incoming.Describe(),
    };
}

/// <summary>The whole mod picture for one transfer.</summary>
public sealed class ModPlan
{
    public List<ModPlanItem> Items { get; } = new();

    public IEnumerable<ModPlanItem> ToInstall => Items.Where(i => i.Action == ModAction.Install);
    public IEnumerable<ModPlanItem> Differing => Items.Where(i => i.Action == ModAction.KeepExisting);
    public IEnumerable<ModPlanItem> Identical => Items.Where(i => i.Action == ModAction.Identical);

    /// <summary>
    /// False when the package named its mods but did not carry them - an older package, or one made
    /// with mod copying switched off. The names are still worth reporting; nothing can be installed.
    /// </summary>
    public bool FilesAvailable { get; set; } = true;

    public bool AnythingToDo => FilesAvailable && (ToInstall.Any() || Items.Any(i => i.ReplaceApproved));

    /// <summary>Mods this PC has that the sender does not. Never removed - only mentioned.</summary>
    public List<ModEntry> ExtraHere { get; } = new();

    public long InstallBytes => ToInstall.Sum(i => i.Incoming.SizeBytes);

    public string Summary()
    {
        int add = ToInstall.Count();
        int diff = Differing.Count();

        if (Items.Count == 0) return "No mods to copy.";
        if (add == 0 && diff == 0) return $"All {Items.Count} mod{Plural(Items.Count)} already match.";

        var parts = new List<string>();
        if (add > 0) parts.Add($"{add} mod{Plural(add)} to add");
        if (diff > 0) parts.Add($"{diff} already here and left alone");
        return string.Join(", ", parts) + ".";
    }

    private static string Plural(int n) => n == 1 ? "" : "s";
}

/// <summary>
/// Finding, comparing and installing mod folders.
///
/// Mods matter here because a save played with mods does not load properly without them, so a save
/// that travels alone is only half the story. The rule throughout is the same as for saves, only
/// stricter: an existing mod folder is never written over. A mod folder usually holds its own
/// settings, and those settings are the one thing the other PC cannot possibly know about.
/// </summary>
public static class Mods
{
    public const string InfoFileName = "ModInfo.xml";

    /// <summary>
    /// Mod folders the game itself installs. Present on every copy of the game, version-matched to
    /// that install, and updated by the game - so they belong to the PC, not to the player.
    /// </summary>
    public static readonly string[] ShippedFolderNames = { "0_TFP_Harmony" };

    /// <summary>
    /// Recognised by folder name or by author, and only inside the game folder - a bundled mod is
    /// something the install put there, and the user-data folder is not where the install writes.
    /// </summary>
    public static bool IsShippedWithGame(string folderName, string author, ModRoot root)
    {
        if (root != ModRoot.Install) return false;

        foreach (var known in ShippedFolderNames)
            if (string.Equals(folderName, known, StringComparison.OrdinalIgnoreCase)) return true;

        return author.Contains("Fun Pimps", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Left behind only by a copy that was interrupted; swept on the next run.</summary>
    public const string PartialSuffix = ".savesync-part";

    public static string UserDataModsDir(GameLocation loc) => Path.Combine(loc.UserDataRoot, "Mods");

    public static string? InstallModsDir(GameLocation loc)
        => string.IsNullOrWhiteSpace(loc.InstallDir) ? null : Path.Combine(loc.InstallDir!, "Mods");

    public static string DirFor(GameLocation loc, ModRoot root)
        => root == ModRoot.Install ? InstallModsDir(loc) ?? UserDataModsDir(loc) : UserDataModsDir(loc);

    /// <summary>
    /// How far below a Mods folder a ModInfo.xml is still believed to be a mod.
    ///
    /// Mods are supposed to sit one level down, but they arrive as zips and people extract them
    /// with an extra wrapper folder, or two, which is the single most common reason a mod "does not
    /// work". Finding those is cheap; refusing to is how a tool ends up looking stupid.
    /// </summary>
    public const int MaxDepth = 4;

    /// <summary>
    /// Every mod on this machine, from both places the game looks, however badly it was extracted.
    ///
    /// <paramref name="hash"/> is what makes "the same mod" mean the same bytes rather than the
    /// same folder name; it costs a full read of every mod file, so the UI asks for it only when a
    /// transfer is actually being planned.
    ///
    /// Nothing in here throws on a bad folder. An unreadable directory, a permission error or a
    /// broken ModInfo.xml costs that one mod, never the scan.
    /// </summary>
    public static List<ModEntry> Enumerate(GameLocation loc, bool hash = false, CancellationToken ct = default)
    {
        var found = new List<ModEntry>();

        foreach (var root in new[] { ModRoot.UserData, ModRoot.Install })
        {
            var dir = root == ModRoot.Install ? InstallModsDir(loc) : UserDataModsDir(loc);
            if (dir is null || !Directory.Exists(dir)) continue;

            Scan(dir, root, 1, found, ct);
        }

        foreach (var entry in found)
        {
            ct.ThrowIfCancellationRequested();
            if (hash) Fingerprint(entry, ct);
        }

        return found.OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Walks one Mods folder. A folder holding ModInfo.xml is a mod and is not descended into;
    /// anything else is opened one level further, up to <see cref="MaxDepth"/>.
    /// </summary>
    private static void Scan(string dir, ModRoot root, int depth, List<ModEntry> found, CancellationToken ct)
    {
        if (depth > MaxDepth) return;

        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        foreach (var sub in subs)
        {
            ct.ThrowIfCancellationRequested();

            var folderName = Path.GetFileName(sub);
            if (folderName.Length == 0) continue;
            if (folderName.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            // The tool's own working folders, and anything Windows or an unzipper left behind.
            if (folderName.StartsWith(".", StringComparison.Ordinal)) continue;
            if (string.Equals(folderName, "__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;

            if (LooksLikeMod(sub))
            {
                // A mod already found under its proper name wins; the game loads the first it sees
                // and a duplicate here would only offer to copy the same thing twice.
                if (found.Any(m => string.Equals(m.FolderName, folderName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var entry = Describe(sub, root);
                entry.NestedDepth = depth;
                found.Add(entry);
                continue;   // a mod is never searched for more mods inside itself
            }

            Scan(sub, root, depth + 1, found, ct);
        }
    }

    /// <summary>
    /// A folder counts as a mod when the game would treat it as one. ModInfo.xml is what the game
    /// itself looks for, so anything else in the Mods folder is somebody's notes or a leftover.
    /// </summary>
    public static bool LooksLikeMod(string folder)
        => File.Exists(Path.Combine(folder, InfoFileName));

    /// <summary>Reads ModInfo.xml. A mod with an unreadable one still counts - it just has no details.</summary>
    public static ModEntry Describe(string folder, ModRoot root)
    {
        var entry = new ModEntry
        {
            FolderName = Path.GetFileName(folder),
            Folder = PathUtil.Normalize(folder),
            Root = root,
        };

        try
        {
            var doc = XDocument.Load(Path.Combine(folder, InfoFileName));

            // The element layout changed between game versions (ModInfo vs xml as the root), so
            // elements are found by name at any depth rather than by an expected path.
            entry.Name = Value(doc, "Name");
            entry.DisplayName = Value(doc, "DisplayName");
            entry.Version = Value(doc, "Version");
            entry.Author = Value(doc, "Author");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Unreadable ModInfo.xml: still a mod, just an anonymous one.
        }

        if (string.IsNullOrWhiteSpace(entry.Name)) entry.Name = entry.FolderName;
        entry.ShippedWithGame = IsShippedWithGame(entry.FolderName, entry.Author, root);

        return entry;
    }

    private static string Value(XDocument doc, string element)
    {
        foreach (var e in doc.Descendants())
        {
            if (!string.Equals(e.Name.LocalName, element, StringComparison.OrdinalIgnoreCase)) continue;

            var attr = e.Attribute("value")?.Value;
            if (!string.IsNullOrWhiteSpace(attr)) return attr.Trim();

            var text = e.Value;
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return "";
    }

    /// <summary>Measures a mod and hashes its contents, so "same mod" can mean "same bytes".</summary>
    public static void Fingerprint(ModEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Folder) || !Directory.Exists(entry.Folder)) return;

        try
        {
            var manifest = Manifest.Build(entry.Folder, null, ct);
            entry.SizeBytes = manifest.TotalBytes;
            entry.FileCount = manifest.Count;
            entry.ContentSha = manifest.ComputeSha();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No hash means "not proven identical", which resolves to leaving the local copy alone.
            entry.ContentSha = "";
        }
    }

    /// <summary>
    /// Works out what applying <paramref name="incoming"/> would do, without touching anything.
    ///
    /// The only outcome that writes to an existing folder is one the user has explicitly approved
    /// afterwards; nothing in here produces that on its own.
    /// </summary>
    public static ModPlan Plan(IEnumerable<ModEntry> local, IEnumerable<ModEntry> incoming)
    {
        var here = local.ToList();
        var plan = new ModPlan();

        foreach (var mod in incoming)
        {
            var match = here.FirstOrDefault(m =>
                string.Equals(m.FolderName, mod.FolderName, StringComparison.OrdinalIgnoreCase));

            ModAction action;
            if (match is null) action = ModAction.Install;
            else if (SameContent(match, mod)) action = ModAction.Identical;
            else action = ModAction.KeepExisting;

            plan.Items.Add(new ModPlanItem { Incoming = mod, Local = match, Action = action });
        }

        foreach (var mine in here)
        {
            if (!incoming.Any(m => string.Equals(m.FolderName, mine.FolderName, StringComparison.OrdinalIgnoreCase)))
                plan.ExtraHere.Add(mine);
        }

        return plan;
    }

    /// <summary>
    /// Same bytes. A missing hash on either side means "not proven identical", which resolves to
    /// leaving the existing folder alone - the safe direction, and the only one this tool takes on
    /// its own.
    /// </summary>
    public static bool SameContent(ModEntry a, ModEntry b)
    {
        if (string.IsNullOrWhiteSpace(a.ContentSha) || string.IsNullOrWhiteSpace(b.ContentSha)) return false;
        return string.Equals(a.ContentSha, b.ContentSha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Plans against what is on this machine right now, hashing as little as possible.
    ///
    /// Hashing every mod folder on every refresh would stall the window for seconds on a large mod
    /// pack. Size and file count are read cheaply, and two folders that differ in either cannot be
    /// identical - so only the ones that still look the same are actually read and hashed.
    /// </summary>
    public static ModPlan PlanAgainstDisk(GameLocation loc, IEnumerable<ModEntry> incoming, CancellationToken ct = default)
    {
        var wanted = incoming.ToList();
        var here = Enumerate(loc, hash: false, ct);

        foreach (var mine in here)
        {
            var match = wanted.FirstOrDefault(m =>
                string.Equals(m.FolderName, mine.FolderName, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;

            Measure(mine);
            if (mine.SizeBytes != match.SizeBytes || mine.FileCount != match.FileCount) continue;

            // Same shape: only now is it worth reading the bytes to tell identical from different.
            Fingerprint(mine, ct);
        }

        return Plan(here, wanted);
    }

    /// <summary>
    /// The mods that should travel with a save: everything a person installed, and nothing the game
    /// installed for itself.
    /// </summary>
    public static List<ModEntry> Travelling(GameLocation loc, bool hash = false, CancellationToken ct = default)
        => Enumerate(loc, hash, ct).Where(m => !m.ShippedWithGame).ToList();

    /// <summary>Size and file count only. No file is opened, so this stays cheap on a big mod pack.</summary>
    public static void Measure(ModEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Folder) || !Directory.Exists(entry.Folder)) return;

        long size = 0;
        int count = 0;
        foreach (var f in Manifest.EnumerateFiles(entry.Folder))
        {
            try { size += new FileInfo(f).Length; count++; }
            catch (IOException) { }
        }
        entry.SizeBytes = size;
        entry.FileCount = count;
    }

    /// <summary>
    /// Clears folders left behind by a copy that was interrupted.
    ///
    /// The partial folder is named so the game ignores it and so this sweep can recognise it. It is
    /// always a fresh copy that was never finished, so deleting it can never lose anything.
    /// </summary>
    public static List<string> SweepPartials(GameLocation loc)
    {
        var notes = new List<string>();

        foreach (var root in new[] { ModRoot.UserData, ModRoot.Install })
        {
            var dir = root == ModRoot.Install ? InstallModsDir(loc) : UserDataModsDir(loc);
            if (dir is null || !Directory.Exists(dir)) continue;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var sub in subs)
            {
                if (!Path.GetFileName(sub).EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    PathUtil.DeleteTree(sub);
                    notes.Add($"Cleared an unfinished mod copy ({Path.GetFileName(sub)}).");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        return notes;
    }
}
