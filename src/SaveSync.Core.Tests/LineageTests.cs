using SaveSync.Core;
using Xunit;

namespace SaveSync.Core.Tests;

/// <summary>
/// The classification matrix. Everything the UI does hangs off these six answers, so each one gets
/// an explicit test including the three-machine cases a version counter would get wrong.
/// </summary>
public class LineageTests
{
    // Version ids are random in production; hardcoding one here would let unrelated histories
    // accidentally share an ancestor and quietly weaken every test below.
    private static Passport Root(string saveId = "save1")
        => new()
        {
            SaveId = saveId,
            World = "Navezgane",
            SaveName = "My Game",
            VersionId = Ids.NewVersionId(),
            Ordinal = 1,
        };

    private static Passport Play(Passport from, string on = "PC\\user")
        => from.NewChild(on, DateTimeOffset.UtcNow);

    [Fact]
    public void NoLocal_when_nothing_here()
        => Assert.Equal(Relation.NoLocal, Lineage.Compare(null, Root()));

    [Fact]
    public void Identical_when_same_version()
    {
        var a = Root();
        Assert.Equal(Relation.Identical, Lineage.Compare(a, a.Clone()));
    }

    [Fact]
    public void FastForward_when_incoming_descends_from_local()
    {
        var baseline = Root();
        var played = Play(baseline);
        Assert.Equal(Relation.FastForward, Lineage.Compare(baseline, played));
        Assert.True(Lineage.IsSafeToApply(Relation.FastForward));
    }

    [Fact]
    public void Stale_when_local_already_contains_incoming()
    {
        var baseline = Root();
        var played = Play(baseline);
        Assert.Equal(Relation.Stale, Lineage.Compare(played, baseline));
        Assert.False(Lineage.IsSafeToApply(Relation.Stale));
    }

    [Fact]
    public void Diverged_when_both_sides_played_from_the_same_point()
    {
        var shared = Root();
        var desktop = Play(shared, "DESKTOP\\ryan");
        var laptop = Play(shared, "LAPTOP\\ryan");

        Assert.Equal(Relation.Diverged, Lineage.Compare(desktop, laptop));
        Assert.Equal(Relation.Diverged, Lineage.Compare(laptop, desktop));
        Assert.False(Lineage.IsSafeToApply(Relation.Diverged));
    }

    [Fact]
    public void Unrelated_when_save_ids_differ()
        => Assert.Equal(Relation.Unrelated, Lineage.Compare(Root("a"), Root("b")));

    /// <summary>
    /// The case a monotonic counter gets wrong. Three machines produce version 2 independently;
    /// ordinals collide, ancestry does not.
    /// </summary>
    [Fact]
    public void Three_machines_from_one_baseline_all_diverge_despite_equal_ordinals()
    {
        var shared = Root();
        var a = Play(shared, "DESKTOP\\ryan");
        var b = Play(shared, "LAPTOP\\ryan");
        var c = Play(shared, "LAPTOP\\partner");

        Assert.Equal(a.Ordinal, b.Ordinal);
        Assert.Equal(b.Ordinal, c.Ordinal);

        Assert.Equal(Relation.Diverged, Lineage.Compare(a, b));
        Assert.Equal(Relation.Diverged, Lineage.Compare(b, c));
        Assert.Equal(Relation.Diverged, Lineage.Compare(a, c));
    }

    /// <summary>A long single-threaded history stays a fast-forward however far behind you are.</summary>
    [Fact]
    public void Long_chain_still_fast_forwards()
    {
        var p = Root();
        var behind = p.Clone();
        for (int i = 0; i < 50; i++) p = Play(p);

        Assert.Equal(Relation.FastForward, Lineage.Compare(behind, p));
    }

    /// <summary>
    /// Past the chain cap the common ancestor is forgotten. That must degrade to Diverged - which
    /// stops and asks - and never to FastForward, which would overwrite.
    /// </summary>
    [Fact]
    public void Beyond_chain_cap_degrades_to_diverged_not_fast_forward()
    {
        var p = Root();
        var behind = p.Clone();
        for (int i = 0; i < Lineage.MaxChain + 20; i++) p = Play(p);

        var relation = Lineage.Compare(behind, p);
        Assert.Equal(Relation.Diverged, relation);
        Assert.False(Lineage.IsSafeToApply(relation));
    }

    [Fact]
    public void Chain_never_exceeds_cap()
    {
        var p = Root();
        for (int i = 0; i < Lineage.MaxChain * 2; i++) p = Play(p);
        Assert.True(p.Chain.Count <= Lineage.MaxChain);
    }

    [Fact]
    public void CommonAncestor_finds_the_split_point()
    {
        var shared = Root();
        var mid = Play(shared);
        var a = Play(mid, "DESKTOP\\ryan");
        var b = Play(mid, "LAPTOP\\ryan");

        Assert.Equal(mid.VersionId, Lineage.CommonAncestor(a, b));
    }

    [Fact]
    public void CommonAncestor_is_null_for_unrelated_histories()
        => Assert.Null(Lineage.CommonAncestor(Root("a"), Root("b")));

    [Fact]
    public void Every_relation_has_plain_english()
    {
        foreach (Relation r in Enum.GetValues<Relation>())
        {
            var text = Lineage.Explain(r);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("Unknown.", text);
        }
    }

    /// <summary>Only NoLocal and FastForward may ever be applied without asking.</summary>
    [Fact]
    public void Only_two_relations_are_auto_applicable()
    {
        var safe = Enum.GetValues<Relation>().Where(Lineage.IsSafeToApply).ToArray();
        Assert.Equal(new[] { Relation.NoLocal, Relation.FastForward }, safe);
    }
}
