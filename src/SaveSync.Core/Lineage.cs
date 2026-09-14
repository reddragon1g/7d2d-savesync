namespace SaveSync.Core;

/// <summary>How an incoming copy of a save relates to what is already on this machine.</summary>
public enum Relation
{
    /// <summary>No save here at all. Safe to install.</summary>
    NoLocal,

    /// <summary>
    /// A save exists here but the tool has never seen it before, so there is no shared history to
    /// reason from. Applying anything over it could destroy real progress, so a human decides.
    /// This is the normal state the first time the tool runs on a machine that already has saves.
    /// </summary>
    Unregistered,

    /// <summary>Same version on both sides. Nothing to do.</summary>
    Identical,

    /// <summary>Incoming descends from local. Safe to apply - the normal, boring case.</summary>
    FastForward,

    /// <summary>Local descends from incoming. Incoming is OLD; applying it would throw away progress.</summary>
    Stale,

    /// <summary>Both sides were played since they last agreed. No merge exists, so a human must choose.</summary>
    Diverged,

    /// <summary>Different save slots entirely. Never auto-apply.</summary>
    Unrelated,
}

/// <summary>
/// Ancestry over version ids.
///
/// A plain incrementing counter is sufficient for exactly two machines and silently wrong for
/// three, so every ordering decision is made from the ancestor chain instead. Ordinal is cosmetic.
/// </summary>
public static class Lineage
{
    /// <summary>
    /// Ancestors kept per passport. Truncation can only ever turn a real FastForward into a
    /// reported Diverged, which stops and asks rather than overwriting. It fails safe by design.
    /// At one commit per play session this is several years of history.
    /// </summary>
    public const int MaxChain = 200;

    public static List<string> PushAncestor(IEnumerable<string> chain, string newest)
    {
        var list = new List<string>(MaxChain) { newest };
        foreach (var c in chain)
        {
            if (list.Count >= MaxChain) break;
            if (!Eq(c, newest)) list.Add(c);
        }
        return list;
    }

    public static Relation Compare(Passport? local, Passport incoming)
    {
        if (local is null) return Relation.NoLocal;
        if (!Eq(local.SaveId, incoming.SaveId)) return Relation.Unrelated;
        if (Eq(local.VersionId, incoming.VersionId)) return Relation.Identical;

        // Incoming lists local as an ancestor => incoming is strictly ahead of us.
        if (Contains(incoming.Chain, local.VersionId)) return Relation.FastForward;

        // Local lists incoming as an ancestor => we already have that version, and more since.
        if (Contains(local.Chain, incoming.VersionId)) return Relation.Stale;

        // Neither contains the other: genuinely divergent, or both chains truncated past their
        // common ancestor. Both resolve the same way - stop, ask, and preserve both sides.
        return Relation.Diverged;
    }

    /// <summary>Most recent shared ancestor, used to explain a divergence in plain English.</summary>
    public static string? CommonAncestor(Passport a, Passport b)
    {
        var bIds = new HashSet<string>(b.Chain.Append(b.VersionId), StringComparer.OrdinalIgnoreCase);
        foreach (var id in a.Chain.Prepend(a.VersionId))
            if (bIds.Contains(id)) return id;
        return null;
    }

    /// <summary>
    /// True only when applying the incoming copy cannot lose anything. Everything else - including
    /// Unregistered, which looks empty but is not - requires a human decision.
    /// </summary>
    public static bool IsSafeToApply(Relation r) => r is Relation.NoLocal or Relation.FastForward;

    /// <summary>Plain-English explanation, written for someone who does not know what a version is.</summary>
    public static string Explain(Relation r) => r switch
    {
        Relation.NoLocal => "This PC does not have this save yet.",
        Relation.Unregistered => "This PC already has a save with this name that has never been linked. Nobody can tell which is newer, so you need to choose.",
        Relation.Identical => "Both PCs already have exactly the same save.",
        Relation.FastForward => "The other copy is newer and includes everything this PC has.",
        Relation.Stale => "The other copy is older than what is on this PC. Installing it would lose progress.",
        Relation.Diverged => "Both PCs were played since they last matched. There is no way to combine them, so you need to pick one.",
        Relation.Unrelated => "These are two different saves that happen to share a name.",
        _ => "Unknown.",
    };

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(IEnumerable<string> chain, string id)
    {
        foreach (var c in chain) if (Eq(c, id)) return true;
        return false;
    }
}
