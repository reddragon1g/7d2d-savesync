namespace SaveSync.Core;

/// <summary>Which way a single save needs to move, if at all.</summary>
public enum SyncDirection
{
    /// <summary>Both sides already match. Nothing to do.</summary>
    UpToDate,

    /// <summary>This PC has the newer copy; it should go onto the stick.</summary>
    ToStick,

    /// <summary>The stick has the newer copy; it should come onto this PC.</summary>
    ToPc,

    /// <summary>No safe answer. A person has to choose, and nothing happens until they do.</summary>
    Conflict,
}

public sealed class SyncItem
{
    public required string World { get; init; }
    public required string SaveName { get; init; }

    public SaveSlot? Local { get; init; }
    public string? PackageDir { get; init; }
    public PackageInfo? Package { get; init; }

    public Relation Relation { get; init; }
    public SyncDirection Direction { get; init; }

    /// <summary>One sentence, in the user's words, for why it is going that way.</summary>
    public required string Reason { get; init; }

    public long Bytes { get; init; }

    public string Display => $"{SaveName} ({World})";
}

/// <summary>
/// What would happen if the user pressed a button right now, for every save at once.
/// </summary>
public sealed class StickPlan
{
    public required string StickRoot { get; init; }
    public List<SyncItem> Items { get; init; } = new();
    public List<Finding> Blockers { get; init; } = new();

    public IEnumerable<SyncItem> ToStick => Items.Where(i => i.Direction == SyncDirection.ToStick);
    public IEnumerable<SyncItem> ToPc => Items.Where(i => i.Direction == SyncDirection.ToPc);
    public IEnumerable<SyncItem> Conflicts => Items.Where(i => i.Direction == SyncDirection.Conflict);
    public IEnumerable<SyncItem> UpToDate => Items.Where(i => i.Direction == SyncDirection.UpToDate);

    public bool HasBlockers => Blockers.Count > 0;
    public bool AnythingToDo => ToStick.Any() || ToPc.Any() || Conflicts.Any();

    public long ToStickBytes => ToStick.Sum(i => i.Bytes);
    public long ToPcBytes => ToPc.Sum(i => i.Bytes);
}

public sealed class SyncOutcome
{
    public List<string> Copied { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<Finding> Findings { get; } = new();
    public List<SyncItem> NeedsChoice { get; } = new();
}

/// <summary>
/// Whole-stick planning, so the app can offer two buttons instead of a decision per save.
///
/// The direction of every save is worked out here rather than asked about. The user only ever sees
/// a question when there genuinely is no safe answer, which in practice means both machines were
/// played since they last agreed.
/// </summary>
public static class StickSync
{
    /// <summary>Works out what each save needs, without touching anything.</summary>
    public static StickPlan Build(TransferEngine engine, string stickRoot, CancellationToken ct = default)
    {
        var plan = new StickPlan { StickRoot = stickRoot };
        plan.Blockers.AddRange(TransferEngine.GlobalBlockers());

        var locals = SaveDiscovery.Enumerate(engine.Location, measure: true, ct);

        // Newest package per save, keyed by SaveId; a stick accumulates several versions over time.
        var packages = new Dictionary<string, (string Dir, PackageInfo Info)>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in TransferEngine.FindPackages(stickRoot))
        {
            ct.ThrowIfCancellationRequested();
            var info = PackageInfo.Load(dir);
            if (info is null) continue;

            var key = info.Passport.SaveId;
            if (!packages.TryGetValue(key, out var existing) || info.Passport.Ordinal > existing.Info.Passport.Ordinal)
                packages[key] = (dir, info);
        }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var local in locals)
        {
            ct.ThrowIfCancellationRequested();

            var match = FindPackageFor(local, packages);
            if (match is not null) claimed.Add(match.Value.Info.Passport.SaveId);

            plan.Items.Add(Classify(local, match?.Dir, match?.Info));
        }

        // Saves that exist only on the stick.
        foreach (var (saveId, entry) in packages)
        {
            if (claimed.Contains(saveId)) continue;
            ct.ThrowIfCancellationRequested();

            plan.Items.Add(new SyncItem
            {
                World = entry.Info.Passport.World,
                SaveName = entry.Info.Passport.SaveName,
                PackageDir = entry.Dir,
                Package = entry.Info,
                Relation = Relation.NoLocal,
                Direction = SyncDirection.ToPc,
                Reason = "This PC does not have this save yet.",
                Bytes = entry.Info.PayloadBytes,
            });
        }

        plan.Items.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        return plan;
    }

    private static (string Dir, PackageInfo Info)? FindPackageFor(
        SaveSlot local, Dictionary<string, (string Dir, PackageInfo Info)> packages)
    {
        if (local.Passport is not null && packages.TryGetValue(local.Passport.SaveId, out var byId))
            return byId;

        // Never linked, or linked independently on each machine: fall back to the name so the two
        // copies are at least put in front of the user together rather than silently duplicated.
        foreach (var entry in packages.Values)
        {
            if (string.Equals(entry.Info.Passport.World, local.World, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Info.Passport.SaveName, local.SaveName, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    private static SyncItem Classify(SaveSlot local, string? packageDir, PackageInfo? package)
    {
        // "Played since we last packaged it" is the common case and the passport alone cannot see
        // it, because a passport is only rewritten when a package is made.
        bool localDirty = TransferEngine.LooksChangedSinceCommit(local);

        if (package is null || packageDir is null)
        {
            return new SyncItem
            {
                World = local.World,
                SaveName = local.SaveName,
                Local = local,
                Relation = Relation.NoLocal,
                Direction = SyncDirection.ToStick,
                Reason = "The stick does not have this save yet.",
                Bytes = local.SizeBytes,
            };
        }

        var relation = SaveDiscovery.Classify(local, package.Passport);

        var (direction, reason) = relation switch
        {
            Relation.Identical when localDirty =>
                (SyncDirection.ToStick, "You have played on this PC since the last copy was made."),
            Relation.Identical =>
                (SyncDirection.UpToDate, "Both copies already match."),

            // The stick is ahead, but this PC was also played: no safe answer.
            Relation.FastForward when localDirty =>
                (SyncDirection.Conflict, "The stick is newer, but this PC was also played since they last matched."),
            Relation.FastForward =>
                (SyncDirection.ToPc, "The stick has the newer copy."),

            Relation.Stale =>
                (SyncDirection.ToStick, "This PC has the newer copy."),

            Relation.Diverged =>
                (SyncDirection.Conflict, "Both this PC and the stick were played since they last matched."),

            Relation.Unregistered =>
                (SyncDirection.Conflict, "This PC already has a save with this name that has never been linked."),

            Relation.Unrelated =>
                (SyncDirection.Conflict, "These are two different saves that happen to share a name."),

            _ => (SyncDirection.Conflict, "Cannot tell which copy is newer."),
        };

        return new SyncItem
        {
            World = local.World,
            SaveName = local.SaveName,
            Local = local,
            PackageDir = packageDir,
            Package = package,
            Relation = relation,
            Direction = direction,
            Reason = reason,
            Bytes = direction == SyncDirection.ToPc ? package.PayloadBytes : local.SizeBytes,
        };
    }

    /// <summary>Copies every save that should go onto the stick. Conflicts are left untouched.</summary>
    public static SyncOutcome CopyToStick(
        TransferEngine engine,
        StickPlan plan,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var outcome = new SyncOutcome();
        if (plan.HasBlockers) throw new TransferBlockedException(plan.Blockers);

        foreach (var item in plan.ToStick)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Local is null) continue;

            var result = engine.Export(item.Local, plan.StickRoot, verifyAfterWrite: true, progress, ct);
            outcome.Copied.Add(item.Display);
            outcome.Findings.AddRange(result.Findings);
        }

        outcome.NeedsChoice.AddRange(plan.Conflicts);
        return outcome;
    }

    /// <summary>Installs every save that should come onto this PC. Conflicts are left untouched.</summary>
    /// <param name="only">
    /// The saves to actually bring over. Null means all of them, which is what the single-save case
    /// and every existing caller wants. A subset is what the picker passes when somebody has three
    /// saves on the stick and only wants two of them - previously one save that needed a decision
    /// held up every other save on the stick.
    /// </param>
    public static SyncOutcome CopyToPc(
        TransferEngine engine,
        StickPlan plan,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default,
        IEnumerable<SyncItem>? only = null)
    {
        var outcome = new SyncOutcome();
        if (plan.HasBlockers) throw new TransferBlockedException(plan.Blockers);

        var wanted = only is null
            ? plan.ToPc.ToList()
            : plan.ToPc.Where(i => only.Any(o => ReferenceEquals(o, i)
                  || (string.Equals(o.World, i.World, StringComparison.OrdinalIgnoreCase)
                      && string.Equals(o.SaveName, i.SaveName, StringComparison.OrdinalIgnoreCase)))).ToList();

        foreach (var item in wanted)
        {
            ct.ThrowIfCancellationRequested();
            if (item.PackageDir is null) continue;

            var importPlan = engine.Inspect(item.PackageDir);

            if (importPlan.HasBlockers)
            {
                outcome.Skipped.Add(item.Display);
                outcome.Findings.AddRange(importPlan.Findings.Where(f => f.Severity == Severity.Blocker));
                continue;
            }

            if (!importPlan.IsOneClickSafe)
            {
                // Changed between planning and now, or needs a decision after all.
                outcome.NeedsChoice.Add(item);
                continue;
            }

            var result = engine.Import(importPlan, ImportChoice.Apply, progress, ct);
            if (result.Applied) outcome.Copied.Add(item.Display);
            outcome.Findings.AddRange(result.Findings);
        }

        outcome.NeedsChoice.AddRange(only is null
            ? plan.Conflicts
            : plan.Conflicts.Where(c => only.Any(o =>
                  string.Equals(o.World, c.World, StringComparison.OrdinalIgnoreCase)
                  && string.Equals(o.SaveName, c.SaveName, StringComparison.OrdinalIgnoreCase))));
        return outcome;
    }
}
