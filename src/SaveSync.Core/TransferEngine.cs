using System.Reflection;

namespace SaveSync.Core;

public enum Severity { Info, Warning, Blocker }

public sealed record Finding(Severity Severity, string Message);

/// <summary>What the user is choosing between when a transfer is not automatically safe.</summary>
public enum ImportChoice
{
    /// <summary>Only legal when the relation is safe on its own.</summary>
    Apply,

    /// <summary>Take the incoming copy and keep the current one as a restorable backup.</summary>
    TakeIncomingKeepBackup,

    /// <summary>Leave this machine alone.</summary>
    KeepLocal,

    /// <summary>
    /// Keep both: install the incoming copy beside the existing one under a different name.
    ///
    /// The way out of a name clash that is not a choice between two games. The new copy gets a
    /// fresh identity rather than inheriting the incoming one, because from this moment it is a
    /// separate save with a separate future and sharing an id would make every later comparison
    /// between them wrong.
    /// </summary>
    InstallAsNewSave,
}

public sealed class ExportResult
{
    public required string PackageDir { get; init; }
    public required Passport Passport { get; init; }
    public long Bytes { get; init; }
    public int Files { get; init; }
    public List<Finding> Findings { get; } = new();
}

public sealed class ImportPlan
{
    public required string PackageDir { get; init; }
    public required PackageInfo Info { get; init; }
    public required Manifest Manifest { get; init; }

    /// <summary>The matching save on this machine, or null when there is none.</summary>
    public SaveSlot? Local { get; init; }

    public Relation Relation { get; init; }
    public List<Finding> Findings { get; } = new();

    public string TargetFolder { get; init; } = "";

    /// <summary>Set when this exact version was already installed here before.</summary>
    public DateTimeOffset? PreviouslyInstalledAt { get; init; }

    /// <summary>
    /// This PC was played since its own copy was last packaged.
    ///
    /// Ancestry cannot see this on its own: a passport is only rewritten when a package is built,
    /// so a save that has been played but not yet copied still describes the older version and an
    /// incoming copy looks like a clean fast-forward. Applying it would silently destroy the
    /// session that was just played, which is the exact failure this tool exists to prevent.
    /// </summary>
    public bool LocalPlayedSinceLastCopy { get; init; }

    /// <summary>
    /// What would happen to the mods. Never affects whether the SAVE can be applied: mods are
    /// additive and reversible, a save is neither, so a mod problem must not block a save that is
    /// otherwise safe.
    /// </summary>
    public ModPlan Mods { get; init; } = new();

    /// <summary>
    /// What the game's own files say about whether these two same-named saves are the same game.
    /// Only worked out when it matters - that is, when a person has to decide something.
    /// </summary>
    public KinshipVerdict? Kinship { get; set; }

    /// <summary>Set to install beside the existing save under this name instead of replacing it.</summary>
    public string? InstallAsName { get; set; }

    /// <summary>
    /// A name that is free right now, for the "keep both" option. Built from who it came from
    /// rather than a number, because "My Game (from Chris)" says which one it is and "My Game 2"
    /// says nothing at all.
    /// </summary>
    public string SuggestedNewName()
    {
        var parent = Path.GetDirectoryName(TargetFolder) ?? "";
        var from = string.IsNullOrWhiteSpace(Info.CreatedBy) ? "other PC" : Info.CreatedBy;

        // A machine identity can arrive as MACHINE\\user; only the person part is worth showing.
        var slash = from.LastIndexOf('\\');
        if (slash >= 0 && slash < from.Length - 1) from = from[(slash + 1)..];

        var baseName = PathUtil.Sanitize($"{Info.Passport.SaveName} (from {from})");
        if (!Directory.Exists(Path.Combine(parent, baseName))) return baseName;

        for (int n = 2; n < 100; n++)
        {
            var candidate = PathUtil.Sanitize($"{Info.Passport.SaveName} (from {from}) {n}");
            if (!Directory.Exists(Path.Combine(parent, candidate))) return candidate;
        }

        return PathUtil.Sanitize($"{Info.Passport.SaveName} {Guid.NewGuid().ToString("N")[..6]}");
    }

    /// <summary>Where the save will actually land, once any rename is taken into account.</summary>
    public string EffectiveTargetFolder
        => string.IsNullOrWhiteSpace(InstallAsName)
            ? TargetFolder
            : Path.Combine(Path.GetDirectoryName(TargetFolder) ?? "", PathUtil.Sanitize(InstallAsName!));

    public bool HasBlockers => Findings.Any(f => f.Severity == Severity.Blocker);

    /// <summary>True when the tool can offer a single unambiguous button instead of a choice.</summary>
    public bool IsOneClickSafe =>
        !HasBlockers
        && Lineage.IsSafeToApply(Relation)
        && !LocalPlayedSinceLastCopy
        && PreviouslyInstalledAt is null;

    /// <summary>True when applying would lose progress unless the user explicitly says so.</summary>
    public bool NeedsHumanChoice =>
        LocalPlayedSinceLastCopy
        || Relation is Relation.Stale or Relation.Diverged or Relation.Unregistered or Relation.Unrelated;
}

public sealed class ImportResult
{
    public bool Applied { get; init; }
    public SnapshotInfo? Backup { get; init; }
    public Passport? Passport { get; init; }
    public List<Finding> Findings { get; } = new();

    /// <summary>Mods actually added to this PC, by folder name.</summary>
    public List<string> ModsInstalled { get; } = new();
}

/// <summary>
/// Builds and applies transfers.
///
/// Every write follows the same shape: stage somewhere disposable, verify every byte against the
/// manifest, park whatever is being replaced, then swap by moving directories within one volume.
/// Nothing is deleted at any point, and the only window where the target is absent is a single
/// directory move that a crash can be reconstructed from.
/// </summary>
public sealed class TransferEngine
{
    private readonly AppConfig _config;

    public TransferEngine(AppConfig config, GameLocation location)
    {
        _config = config;
        Location = location;
        Workspace = new Workspace(location.UserDataRoot);
        Workspace.EnsureCreated();
        Snapshots = new SnapshotStore(Workspace, config.SnapshotsToKeep);
    }

    public GameLocation Location { get; }
    public Workspace Workspace { get; }
    public SnapshotStore Snapshots { get; }

    /// <summary>
    /// Who is using the tool - a person's name when a profile is selected. Falls back to the
    /// machine identity so the engine still works with no profile at all.
    /// </summary>
    public string Identity { get; set; } = Machine.Identity;

    public static string ToolVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Overridable so the game-running guard itself can be tested. Production always uses the real
    /// process check.
    /// </summary>
    public static Func<bool> IsGameRunningProbe { get; set; } = GamePaths.IsGameRunning;

    /// <summary>
    /// Serialises everything that modifies a save.
    ///
    /// A transfer can begin from three places at once - somebody pressing a button, a package
    /// arriving over the network, and the background watcher acting on what it found - and two of
    /// them parking and swapping the same folder together would leave a mess no amount of later
    /// verification could untangle. Nobody needs transfers to run in parallel, so they take turns.
    /// </summary>
    private static readonly object MutationGate = new();

    /// <summary>Reasons no transfer may run right now, in the user's words.</summary>
    public static List<Finding> GlobalBlockers()
    {
        var list = new List<Finding>();
        if (IsGameRunningProbe())
            list.Add(new Finding(Severity.Blocker,
                "7 Days to Die is running. Close the game first - copying a save while it is open produces a broken world."));
        return list;
    }

    /// <summary>
    /// How far two recordings of the same file's timestamp may differ and still be the same file.
    ///
    /// USB sticks are normally FAT32 or exFAT, which store timestamps far more coarsely than NTFS
    /// - FAT32 to the nearest two seconds. A save copied out to a stick and back therefore returns
    /// with a slightly different timestamp than it left with, and an exact comparison would call
    /// every single stick transfer "played since the last copy" and invent a conflict every time.
    ///
    /// Playing takes minutes, so this window cannot hide a real session.
    /// </summary>
    public static readonly TimeSpan FileTimeTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Has this save been played since its contents were last recorded? Uses timestamps rather
    /// than hashing so it can run on every UI refresh without reading gigabytes.
    ///
    /// Compares a file time against the file time captured at commit, never against a wall clock.
    /// An earlier version compared against CommittedAt, which was wrong in two ways: after an
    /// import CommittedAt belongs to the machine that sent the save, and two PCs rarely agree on
    /// the time to the second. Both produced saves that looked played the moment they arrived.
    /// </summary>
    public static bool LooksChangedSinceCommit(SaveSlot slot)
    {
        if (slot.Passport is null) return true;
        if (slot.LastWriteUtc == DateTimeOffset.MinValue) return false;

        if (slot.Passport.ContentMTimeTicks > 0)
        {
            var drift = Math.Abs(slot.LastWriteUtc.UtcTicks - slot.Passport.ContentMTimeTicks);
            return drift > FileTimeTolerance.Ticks;
        }

        // Written before content times were recorded: fall back to the old comparison, which is
        // imperfect but still errs towards asking rather than overwriting.
        return slot.LastWriteUtc > slot.Passport.CommittedAt.AddSeconds(2);
    }

    // ------------------------------------------------------------------ sending

    /// <summary>
    /// Records the current contents as a new version when they differ from what the passport
    /// describes. Called immediately before building a package, so the version that travels is
    /// always the version that was played.
    /// </summary>
    public Passport CommitLocalVersion(
        SaveSlot slot,
        Manifest manifest,
        string gameVersion,
        IProgress<ScanProgress>? progress = null)
    {
        // First time we have seen this save: give it an identity now rather than making the user
        // perform a separate "set up" step. Two machines adopting the same-named save independently
        // produce unrelated histories, which is safe because the receiving side then reports
        // Unrelated and asks rather than overwriting.
        slot.Passport ??= SaveDiscovery.Adopt(slot, Location, gameVersion, Identity);

        lock (MutationGate)
        {
            return CommitLocked(slot, manifest, gameVersion);
        }
    }

    private Passport CommitLocked(SaveSlot slot, Manifest manifest, string gameVersion)
    {
        var sha = manifest.ComputeSha();
        var passport = slot.Passport!;

        if (string.Equals(passport.ManifestSha, sha, StringComparison.OrdinalIgnoreCase))
            return passport; // unchanged since the last recorded version; keep the same identity

        var next = passport.NewChild(
            Identity,
            slot.LastWriteUtc == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : slot.LastWriteUtc);

        next.ManifestSha = sha;
        next.SizeBytes = manifest.TotalBytes;
        next.FileCount = manifest.Count;
        next.ContentMTimeTicks = slot.LastWriteUtc == DateTimeOffset.MinValue ? 0 : slot.LastWriteUtc.UtcTicks;
        next.WorldKind = slot.WorldKind;
        next.MachineTag = Machine.Identity;
        if (!string.IsNullOrWhiteSpace(gameVersion)) next.GameVersion = gameVersion;

        next.Save(slot.Folder);
        slot.Passport = next;
        return next;
    }

    /// <summary>
    /// Writes a self-contained package for <paramref name="slot"/> into <paramref name="destinationRoot"/>.
    /// </summary>
    public ExportResult Export(
        SaveSlot slot,
        string destinationRoot,
        bool verifyAfterWrite = true,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var blockers = GlobalBlockers();
        if (blockers.Count > 0) throw new TransferBlockedException(blockers);

        var gameVersion = SaveDiscovery.ReadGameVersionHint(Location);
        var manifest = Manifest.Build(slot.Folder, progress, ct);
        var passport = CommitLocalVersion(slot, manifest, gameVersion, progress);

        // Mods are read before the space check because they are usually the larger half of a
        // modded transfer, and a stick that fills up half way through is the worst outcome here.
        var mods = _config.IncludeMods ? Mods.Travelling(Location, hash: true, ct) : new List<ModEntry>();
        long modBytes = mods.Sum(m => m.SizeBytes);

        // Short folder names on purpose. A save's own path is already long, and Windows still
        // refuses anything past 260 characters unless long paths are switched on machine-wide -
        // which is not something to rely on for somebody else's PC. Nothing reads these names;
        // identity lives in package.json.
        var packageDir = Path.Combine(
            destinationRoot,
            PackageLayout.RootFolderName,
            PathUtil.Sanitize(Shorten(passport.SaveId, 12)),
            PathUtil.Sanitize($"v{passport.Ordinal}_{Shorten(passport.VersionId, 8)}"));

        // Space check before writing anything, so a full stick fails up front rather than half way.
        var need = manifest.TotalBytes + modBytes + (slot.NeedsGeneratedWorld ? GeneratedWorldSize(slot) : 0);
        var free = FileOps.FreeSpace(destinationRoot);
        if (free >= 0 && free < need + (64L * 1024 * 1024))
        {
            throw new TransferBlockedException(new List<Finding>
            {
                new(Severity.Blocker,
                    $"Not enough room. This save needs {PathUtil.HumanBytes(need)} and there is {PathUtil.HumanBytes(free)} free."),
            });
        }

        if (Directory.Exists(packageDir)) PathUtil.DeleteTree(packageDir);
        Directory.CreateDirectory(packageDir);

        FileOps.CopyTree(slot.Folder, PackageLayout.Payload(packageDir), progress, ct);

        var result = new ExportResult
        {
            PackageDir = packageDir,
            Passport = passport,
            Bytes = manifest.TotalBytes,
            Files = manifest.Count,
        };

        bool includesWorld = false;
        if (slot.NeedsGeneratedWorld)
        {
            var src = Path.Combine(Location.GeneratedWorldsDir, slot.World);
            if (Directory.Exists(src))
            {
                FileOps.CopyTree(src, PackageLayout.World(packageDir), progress, ct);
                includesWorld = true;
            }
            else
            {
                result.Findings.Add(new Finding(Severity.Warning,
                    $"This save uses the custom world \"{slot.World}\" but its map data was not found on this PC. "
                    + "The save may not load on another machine."));
            }
        }

        bool includesModFiles = false;
        if (mods.Count > 0)
        {
            var packed = new List<ModEntry>();
            foreach (var mod in mods)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(PackageLayout.Mods(packageDir), PathUtil.Sanitize(mod.FolderName));
                try
                {
                    FileOps.CopyTree(mod.Folder, dest, progress, ct);
                    packed.Add(mod);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Skip it rather than abandoning the save. A mod left behind is recoverable;
                    // a save that never got packaged because of somebody else's locked file is not.
                    try { PathUtil.DeleteTree(dest); } catch (IOException) { }
                    result.Findings.Add(new Finding(Severity.Warning,
                        $"Could not read the mod {mod.Label}, so it was left out: {e.Message}"));
                }
            }

            mods = packed;
            modBytes = mods.Sum(m => m.SizeBytes);

            // The mods tree gets its own manifest for exactly the same reason the save does:
            // nothing is installed from it until every byte has been checked.
            var modManifest = Manifest.Build(PackageLayout.Mods(packageDir), progress, ct);
            Json.WriteFileAtomic(PackageLayout.ModsManifest(packageDir), modManifest);
            Json.WriteFileAtomic(PackageLayout.ModsList(packageDir), mods);
            includesModFiles = true;

            result.Findings.Add(new Finding(Severity.Info,
                $"{mods.Count} mod{(mods.Count == 1 ? "" : "s")} packaged with this save "
                + $"({PathUtil.HumanBytes(modBytes)})."));
        }

        Json.WriteFileAtomic(PackageLayout.Manifest(packageDir), manifest);

        new PackageInfo
        {
            Passport = passport,
            IncludesGeneratedWorld = includesWorld,
            Mods = mods,
            IncludesModFiles = includesModFiles,
            ModBytes = modBytes,
            CreatedBy = Identity,
            CreatedOnMachineId = _config.MachineId,
            CreatedAt = DateTimeOffset.UtcNow,
            ToolVersion = ToolVersion,
            GameVersion = gameVersion,
            PayloadBytes = manifest.TotalBytes,
            PayloadFiles = manifest.Count,
        }.Save(packageDir);

        if (verifyAfterWrite)
        {
            var problems = manifest.Verify(PackageLayout.Payload(packageDir), progress, ct);
            if (problems.Count > 0)
            {
                PathUtil.DeleteTree(packageDir);
                throw new TransferBlockedException(new List<Finding>
                {
                    new(Severity.Blocker,
                        $"The copy did not come out right ({problems.Count} file(s) differ). Nothing was left behind. "
                        + "This usually means a failing USB stick or drive."),
                });
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ receiving

    /// <summary>Reads a package and works out what applying it would mean, without touching anything.</summary>
    public ImportPlan Inspect(string packageDir)
    {
        var info = PackageInfo.Load(packageDir)
                   ?? throw new InvalidOperationException("That folder is not a SaveSync package.");
        var manifest = Json.ReadFile<Manifest>(PackageLayout.Manifest(packageDir))
                       ?? throw new InvalidOperationException("The package is missing its file list and cannot be trusted.");

        var incoming = info.Passport;
        var local = FindLocal(incoming);
        var relation = SaveDiscovery.Classify(local, incoming);

        var target = local?.Folder
                     ?? Path.Combine(Location.SavesDir, PathUtil.Sanitize(incoming.World), PathUtil.Sanitize(incoming.SaveName));

        var ledger = ConsumedLedger.Load(Workspace);
        var previously = ledger.WhenInstalled(incoming.SaveId, incoming.VersionId);

        // Only counts when there is something here to lose; a brand new save cannot be "dirty".
        bool localDirty = local is not null
                          && local.Passport is not null
                          && LooksChangedSinceCommit(local);

        var modPlan = Mods.PlanAgainstDisk(Location, info.Mods);
        modPlan.FilesAvailable = info.IncludesModFiles && Directory.Exists(PackageLayout.Mods(packageDir));

        var plan = new ImportPlan
        {
            PackageDir = packageDir,
            Info = info,
            Manifest = manifest,
            Local = local,
            Relation = relation,
            TargetFolder = target,
            PreviouslyInstalledAt = previously,
            LocalPlayedSinceLastCopy = localDirty,
            Mods = modPlan,
        };

        // Only when it matters. Reading the evidence means opening the world file, the player list
        // and every shared character file, which is not work to do on a transfer nobody has to
        // think about.
        if (plan.NeedsHumanChoice && local is not null)
        {
            var verdict = SaveKinship.Compare(
                SaveEvidence.Read(local.Folder),
                SaveEvidence.Read(PackageLayout.Payload(packageDir)));

            plan.Kinship = verdict;

            plan.Findings.Add(new Finding(
                verdict.ProvenDifferent ? Severity.Warning : Severity.Info,
                verdict.Headline + (verdict.Reasons.Count > 0 ? " " + string.Join(" ", verdict.Reasons) : "")));
        }

        // Mods are reported, never used to block: they are additive and undoable, a save is not.
        int missing = modPlan.ToInstall.Count();
        if (missing > 0 && !modPlan.FilesAvailable)
        {
            var names = string.Join(", ", modPlan.ToInstall.Take(4).Select(i => i.Incoming.Label));
            plan.Findings.Add(new Finding(Severity.Warning,
                $"That save was played with {missing} mod{(missing == 1 ? "" : "s")} this PC does not have "
                + $"({names}{(missing > 4 ? ", ..." : "")}), and this package does not carry the mod files. "
                + "The save may not load correctly until those mods are installed here."));
        }
        else if (missing > 0)
        {
            plan.Findings.Add(new Finding(Severity.Info,
                $"{missing} mod{(missing == 1 ? " that this PC does not have will be added with it" : "s that this PC does not have will be added with it")} "
                + $"({PathUtil.HumanBytes(modPlan.InstallBytes)})."));
        }

        int kept = modPlan.Differing.Count();
        if (kept > 0)
        {
            plan.Findings.Add(new Finding(Severity.Info,
                $"{kept} mod{(kept == 1 ? " is" : "s are")} already installed here in a different version or with "
                + "different settings. Yours will be left exactly as they are."));
        }

        if (localDirty && Lineage.IsSafeToApply(relation))
        {
            plan.Findings.Add(new Finding(Severity.Warning,
                "This PC has been played since its copy was last sent anywhere. The incoming save is "
                + "newer than what was sent, but it does not include tonight's session on this PC, so "
                + "somebody has to choose."));
        }

        foreach (var b in GlobalBlockers()) plan.Findings.Add(b);

        if (info.Schema > 1)
            plan.Findings.Add(new Finding(Severity.Blocker,
                "This package was made by a newer version of SaveSync. Update this PC first."));

        // A custom world that is not already here, and not carried in the package, will not load.
        if (incoming.WorldKind == WorldKind.Generated || info.IncludesGeneratedWorld)
        {
            var haveWorld = Directory.Exists(Path.Combine(Location.GeneratedWorldsDir, incoming.World));
            var packHasWorld = Directory.Exists(PackageLayout.World(packageDir));
            if (!haveWorld && !packHasWorld)
            {
                plan.Findings.Add(new Finding(Severity.Blocker,
                    $"This save uses the custom world \"{incoming.World}\", which this PC does not have and the "
                    + "package does not include. Installing it would produce a save that will not load."));
            }
        }

        if (!string.IsNullOrWhiteSpace(info.GameVersion))
        {
            var here = SaveDiscovery.ReadGameVersionHint(Location);
            if (!string.IsNullOrWhiteSpace(here)
                && !string.Equals(here, info.GameVersion, StringComparison.OrdinalIgnoreCase))
            {
                plan.Findings.Add(new Finding(Severity.Warning,
                    $"That save was played on game version {info.GameVersion} and this PC last saw {here}. "
                    + "Loading a save from a newer game version can break it."));
            }
        }

        var free = FileOps.FreeSpace(Location.SavesDir);
        if (free >= 0 && free < manifest.TotalBytes + (256L * 1024 * 1024))
        {
            plan.Findings.Add(new Finding(Severity.Blocker,
                $"Not enough disk space. This save needs {PathUtil.HumanBytes(manifest.TotalBytes)} "
                + $"and there is {PathUtil.HumanBytes(free)} free."));
        }

        if (previously is not null)
        {
            plan.Findings.Add(new Finding(Severity.Warning,
                $"This exact copy was already installed on this PC on {previously.Value.ToLocalTime():ddd d MMM, HH:mm}. "
                + "Installing it again would undo anything played since."));
        }

        if (relation == Relation.Stale)
            plan.Findings.Add(new Finding(Severity.Warning, Lineage.Explain(relation)));

        return plan;
    }

    /// <summary>
    /// Applies a package. Verifies the staged copy before anything in the live folder is touched,
    /// and keeps the replaced save as a restorable backup.
    /// </summary>
    public ImportResult Import(
        ImportPlan plan,
        ImportChoice choice,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (choice == ImportChoice.KeepLocal)
            return new ImportResult { Applied = false };

        lock (MutationGate)
        {
            return ImportLocked(plan, choice, progress, ct);
        }
    }

    private ImportResult ImportLocked(
        ImportPlan plan,
        ImportChoice choice,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {

        if (plan.HasBlockers)
            throw new TransferBlockedException(plan.Findings.Where(f => f.Severity == Severity.Blocker).ToList());

        if (choice == ImportChoice.Apply && plan.NeedsHumanChoice)
            throw new InvalidOperationException("This transfer needs an explicit decision before it can run.");

        if (choice == ImportChoice.InstallAsNewSave)
        {
            if (string.IsNullOrWhiteSpace(plan.InstallAsName))
                throw new InvalidOperationException("No name was given for the new save.");

            // Keeping both means landing somewhere nothing lives. Anything else is a replacement
            // wearing a different word, and replacements go through the path that takes backups.
            if (Directory.Exists(plan.EffectiveTargetFolder))
                throw new TransferBlockedException(new List<Finding>
                {
                    new(Severity.Blocker,
                        $"There is already a save called \"{plan.InstallAsName}\" in {plan.Info.Passport.World}. "
                        + "Pick a different name."),
                });
        }

        var result = new ImportResult();
        Workspace.EnsureCreated();

        // Stage onto the same volume as the live saves so the final swap is an atomic move rather
        // than a long copy that can be interrupted half way.
        var staging = Path.Combine(Workspace.Staging, Guid.NewGuid().ToString("N"));

        try
        {
            FileOps.CopyTree(PackageLayout.Payload(plan.PackageDir), staging, progress, ct);

            var problems = plan.Manifest.Verify(staging, progress, ct);
            if (problems.Count > 0)
            {
                PathUtil.DeleteTree(staging);

                // Work out whether the source was already bad, so the message names the real cause.
                var sourceProblems = plan.Manifest.Verify(PackageLayout.Payload(plan.PackageDir), null, ct);
                var cause = sourceProblems.Count > 0
                    ? "The package itself is damaged - the USB stick or the original copy is at fault."
                    : "The copy onto this PC came out wrong - the drive may be failing.";

                throw new TransferBlockedException(new List<Finding>
                {
                    new(Severity.Blocker,
                        $"Verification failed on {problems.Count} file(s), so nothing was changed. {cause}"),
                });
            }

            InstallGeneratedWorldIfNeeded(plan, result, progress, ct);

            // Checking a large save takes a while. If the game was started in the meantime, stop
            // now - before anything live is touched - rather than writing underneath it.
            if (IsGameRunningProbe())
            {
                throw new TransferBlockedException(new List<Finding>
                {
                    new(Severity.Blocker,
                        "7 Days to Die was started while this was being checked, so nothing was changed. "
                        + "Close the game and try again."),
                });
            }

            // Mods first. They only ever ADD folders, so a failure here leaves the save untouched,
            // whereas a save swapped in without its mods is a world that will not load properly.
            InstallMods(plan, result, progress, ct);

            // Everything below works on where the save will actually land, which is not the
            // original folder when the user chose to keep both.
            var target = plan.EffectiveTargetFolder;

            string? parked = null;
            if (Directory.Exists(target))
            {
                parked = Snapshots.Park(
                    target,
                    plan.Local?.Passport?.SaveId ?? plan.Info.Passport.SaveId,
                    plan.Info.Passport.World,
                    plan.Info.Passport.SaveName,
                    $"replaced by a transfer from {plan.Info.CreatedBy}");
            }

            try
            {
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                FileOps.MoveTree(staging, target, ct);
            }
            catch
            {
                // Put the original back before surfacing the failure.
                if (parked is not null)
                {
                    try { PathUtil.DeleteTree(target); } catch (IOException) { }
                    Snapshots.Unpark(parked);
                }
                throw;
            }

            var applied = plan.Info.Passport.Clone();

            if (choice == ImportChoice.InstallAsNewSave)
            {
                // A fresh identity, not the incoming one. From here these are two separate saves
                // with separate futures; sharing an id would make every later comparison between
                // them answer about the wrong save.
                applied.SaveId = Ids.NewSaveId();
                applied.VersionId = Ids.NewVersionId();
                applied.Chain = new List<string>();
                applied.Ordinal = 1;
                applied.SaveName = PathUtil.Sanitize(plan.InstallAsName!);
            }

            applied.Save(target);

            SnapshotInfo? backup = null;
            if (parked is not null)
            {
                // The losing side is pinned whenever it is the only copy of that history. Unrelated
                // belongs here most of all: that folder was never an older version of the incoming
                // save, it was a separate game, and quite possibly somebody else's.
                bool pin = plan.Relation is Relation.Diverged or Relation.Stale
                                         or Relation.Unregistered or Relation.Unrelated;
                backup = Snapshots.Commit(parked, $"replaced by a transfer from {plan.Info.CreatedBy}", pin);
            }

            var ledger = ConsumedLedger.Load(Workspace);
            ledger.Record(applied.SaveId, applied.VersionId);
            ledger.Save(Workspace);
            MarkPackageConsumed(plan.PackageDir);

            var final = new ImportResult { Applied = true, Backup = backup, Passport = applied };
            foreach (var f in result.Findings) final.Findings.Add(f);
            foreach (var m in result.ModsInstalled) final.ModsInstalled.Add(m);
            if (backup is not null)
                final.Findings.Add(new Finding(Severity.Info,
                    $"The previous save on this PC was kept as a backup you can restore ({backup.Describe()})."));

            return final;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { PathUtil.DeleteTree(staging); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Adds the mods this PC does not have, and only those.
    ///
    /// Every failure in here is contained: a mod that cannot be copied is reported and skipped, and
    /// the save transfer carries on. That is deliberate. Mods are additive and reinstallable; a
    /// half-applied save is not, so a mod problem must never take the save down with it.
    /// </summary>
    private void InstallMods(ImportPlan plan, ImportResult result, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var modPlan = plan.Mods;
        if (!modPlan.FilesAvailable) return;

        var modsRoot = PackageLayout.Mods(plan.PackageDir);
        if (!Directory.Exists(modsRoot)) return;

        var wanted = modPlan.Items
            .Where(i => i.Action == ModAction.Install || (i.Action == ModAction.KeepExisting && i.ReplaceApproved))
            .ToList();
        if (wanted.Count == 0) return;

        // Same rule as the save: nothing is installed from a package until every byte checks out.
        var modManifest = Json.ReadFile<Manifest>(PackageLayout.ModsManifest(plan.PackageDir));
        if (modManifest is not null)
        {
            var problems = modManifest.Verify(modsRoot, progress, ct);
            if (problems.Count > 0)
            {
                result.Findings.Add(new Finding(Severity.Warning,
                    $"The mods in this package did not check out ({problems.Count} file(s) wrong), so no mods "
                    + "were installed. The save itself was not affected."));
                return;
            }
        }

        foreach (var item in wanted)
        {
            ct.ThrowIfCancellationRequested();

            var folderName = PathUtil.Sanitize(item.Incoming.FolderName);
            var source = Path.Combine(modsRoot, folderName);
            if (!Directory.Exists(source))
            {
                result.Findings.Add(new Finding(Severity.Warning,
                    $"{item.Incoming.Label} was listed in the package but its files are missing, so it was skipped."));
                continue;
            }

            try { InstallOneMod(item, source, result, progress, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                result.Findings.Add(new Finding(Severity.Warning,
                    $"Could not install {item.Incoming.Label}: {e.Message} The save transfer carried on regardless."));
            }
        }
    }

    /// <summary>
    /// Copies one mod into place. The copy lands under a temporary name first and is renamed only
    /// once it is complete, so the game can never find a half-copied mod - an interrupted copy
    /// leaves a folder this tool recognises and clears, not a mod that loads and breaks the game.
    /// </summary>
    private void InstallOneMod(ModPlanItem item, string source, ImportResult result,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var targetRoot = Mods.DirFor(Location, item.Incoming.Root);
        Directory.CreateDirectory(targetRoot);

        var folderName = PathUtil.Sanitize(item.Incoming.FolderName);
        var target = Path.Combine(targetRoot, folderName);
        var partial = target + Mods.PartialSuffix;

        if (Directory.Exists(partial)) PathUtil.DeleteTree(partial);

        try
        {
            FileOps.CopyTree(source, partial, progress, ct);
        }
        catch
        {
            // Never leave a half-copied folder where the game might find it.
            try { PathUtil.DeleteTree(partial); } catch (IOException) { }
            throw;
        }

        if (Directory.Exists(target))
        {
            // Only reachable when the user explicitly approved replacing this one. The old folder
            // is moved somewhere the game will not look, never deleted.
            var keep = Path.Combine(Workspace.ModBackups, folderName,
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);

            try
            {
                Directory.Move(target, keep);
            }
            catch (IOException)
            {
                // Different volume, or locked. Copy it out rather than leaving it unprotected.
                FileOps.CopyTree(target, keep, null, ct);
                PathUtil.DeleteTree(target);
            }

            result.Findings.Add(new Finding(Severity.Info,
                $"Your existing {item.Incoming.Label} was moved to the mod backups folder before the "
                + "new one went in. Nothing was deleted."));
        }

        Directory.Move(partial, target);
        result.ModsInstalled.Add(item.Incoming.Label);
    }

    private void InstallGeneratedWorldIfNeeded(
        ImportPlan plan, ImportResult result, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var packWorld = PackageLayout.World(plan.PackageDir);
        if (!Directory.Exists(packWorld)) return;

        var target = Path.Combine(Location.GeneratedWorldsDir, PathUtil.Sanitize(plan.Info.Passport.World));
        if (Directory.Exists(target))
        {
            result.Findings.Add(new Finding(Severity.Info,
                $"This PC already has the world \"{plan.Info.Passport.World}\", so its map data was left as it is."));
            return;
        }

        Directory.CreateDirectory(Location.GeneratedWorldsDir);
        FileOps.CopyTree(packWorld, target, progress, ct);
        result.Findings.Add(new Finding(Severity.Info,
            $"Installed the custom world \"{plan.Info.Passport.World}\", which this PC did not have."));
    }

    /// <summary>Best-effort note on the package itself. The local ledger is the authority.</summary>
    private void MarkPackageConsumed(string packageDir)
    {
        try
        {
            var path = Path.Combine(packageDir, ConsumedLedger.FileName);
            var ledger = Json.ReadFile<ConsumedLedger>(path) ?? new ConsumedLedger();
            var info = PackageInfo.Load(packageDir);
            if (info is null) return;
            ledger.Record(info.Passport.SaveId, info.Passport.VersionId);
            Json.WriteFileAtomic(path, ledger);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Write-protected stick. Expected, and already covered by the local ledger.
        }
    }

    /// <summary>Matches on SaveId first so a renamed save still pairs up, then falls back to the name.</summary>
    /// <summary>
    /// Everything that needs putting right after a crash, a kill, or a pulled USB stick: parked
    /// saves go back or become backups, and half-copied mod folders are cleared.
    ///
    /// Run at startup, before anything is shown, so the tool never reports a state it is about to
    /// change. Each half is independent - a failure in one is reported and the other still runs.
    /// </summary>
    public List<string> RecoverInterrupted()
    {
        var notes = new List<string>();

        try { notes.AddRange(Snapshots.RecoverInterrupted()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notes.Add("Could not finish tidying up a previous transfer: " + e.Message);
        }

        try { notes.AddRange(Mods.SweepPartials(Location)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notes.Add("Could not clear an unfinished mod copy: " + e.Message);
        }

        return notes;
    }

    public SaveSlot? FindLocal(Passport incoming)
    {
        var all = SaveDiscovery.Enumerate(Location, measure: true);

        var byId = all.FirstOrDefault(s =>
            s.Passport is not null
            && string.Equals(s.Passport.SaveId, incoming.SaveId, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;

        return all.FirstOrDefault(s =>
            string.Equals(s.World, incoming.World, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.SaveName, incoming.SaveName, StringComparison.OrdinalIgnoreCase));
    }

    private static string Shorten(string value, int length)
        => string.IsNullOrEmpty(value) ? "x" : value.Length <= length ? value : value[..length];

    private long GeneratedWorldSize(SaveSlot slot)
    {
        var src = Path.Combine(Location.GeneratedWorldsDir, slot.World);
        return Directory.Exists(src) ? FileOps.TreeSize(src) : 0;
    }

    /// <summary>Every package found under a drive or folder, newest first.</summary>
    public static List<string> FindPackages(string root)
    {
        var found = new List<string>();
        var start = Path.Combine(root, PackageLayout.RootFolderName);
        if (!Directory.Exists(start)) return found;

        try
        {
            foreach (var saveDir in Directory.GetDirectories(start))
            foreach (var verDir in Directory.GetDirectories(saveDir))
                if (PackageLayout.IsPackage(verDir)) found.Add(verDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        return found
            .OrderByDescending(d => PackageInfo.Load(d)?.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }
}

public sealed class TransferBlockedException : Exception
{
    public TransferBlockedException(List<Finding> findings)
        : base(string.Join(" ", findings.Select(f => f.Message)))
        => Findings = findings;

    public List<Finding> Findings { get; }
}
