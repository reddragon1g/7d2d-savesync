using SaveSync.Core;
using Xunit;

namespace SaveSync.Core.Tests;

/// <summary>
/// Whole-stick planning: the layer that lets the app offer two buttons instead of asking a
/// question per save. The direction of every save has to be right without anyone being consulted.
/// </summary>
public class StickSyncTests : IDisposable
{
    private readonly TestEnv _desktop = new();
    private readonly TestEnv _laptop = new();
    private readonly string _stick;

    public StickSyncTests()
    {
        _stick = Path.Combine(Path.GetTempPath(), "savesync-tests", "stick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stick);
    }

    public void Dispose()
    {
        _desktop.Dispose();
        _laptop.Dispose();
        try { PathUtil.DeleteTree(_stick); } catch (IOException) { }
    }

    private StickPlan Plan(TestEnv env) => StickSync.Build(env.NewEngine(), _stick);

    [Fact]
    public void A_save_the_stick_does_not_have_goes_to_the_stick()
    {
        _desktop.MakeSave();

        var plan = Plan(_desktop);
        var item = Assert.Single(plan.Items);

        Assert.Equal(SyncDirection.ToStick, item.Direction);
        Assert.True(plan.AnythingToDo);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void After_copying_to_the_stick_there_is_nothing_left_to_do()
    {
        _desktop.MakeSave();
        StickSync.CopyToStick(_desktop.NewEngine(), Plan(_desktop));

        var plan = Plan(_desktop);
        Assert.Equal(SyncDirection.UpToDate, Assert.Single(plan.Items).Direction);
        Assert.False(plan.AnythingToDo);
    }

    [Fact]
    public void A_save_only_on_the_stick_comes_onto_this_PC()
    {
        _desktop.MakeSave();
        StickSync.CopyToStick(_desktop.NewEngine(), Plan(_desktop));

        var plan = Plan(_laptop);
        var item = Assert.Single(plan.Items);

        Assert.Equal(SyncDirection.ToPc, item.Direction);

        var outcome = StickSync.CopyToPc(_laptop.NewEngine(), plan);
        Assert.Single(outcome.Copied);
        Assert.Empty(outcome.NeedsChoice);
        Assert.NotNull(_laptop.Slot().Passport);
    }

    /// <summary>The whole point of the travel case: play away, come home, one button puts it back.</summary>
    [Fact]
    public void Played_on_the_laptop_then_brought_home_flows_back_to_the_desktop()
    {
        _desktop.MakeSave();
        StickSync.CopyToStick(_desktop.NewEngine(), Plan(_desktop));
        StickSync.CopyToPc(_laptop.NewEngine(), Plan(_laptop));

        _laptop.Play(_laptop.Slot().Folder, seed: 21);

        var laptopPlan = Plan(_laptop);
        Assert.Equal(SyncDirection.ToStick, Assert.Single(laptopPlan.Items).Direction);
        StickSync.CopyToStick(_laptop.NewEngine(), laptopPlan);

        var desktopPlan = Plan(_desktop);
        Assert.Equal(SyncDirection.ToPc, Assert.Single(desktopPlan.Items).Direction);

        var outcome = StickSync.CopyToPc(_desktop.NewEngine(), desktopPlan);
        Assert.Single(outcome.Copied);
        Assert.Empty(outcome.NeedsChoice);
    }

    /// <summary>
    /// Played on this PC since the last copy was made. The passport alone cannot see this, because
    /// a passport is only rewritten when a package is built.
    /// </summary>
    [Fact]
    public void Playing_after_a_copy_sends_it_back_to_the_stick()
    {
        _desktop.MakeSave();
        StickSync.CopyToStick(_desktop.NewEngine(), Plan(_desktop));

        _desktop.Play(_desktop.Slot().Folder, seed: 8);

        var item = Assert.Single(Plan(_desktop).Items);
        Assert.Equal(SyncDirection.ToStick, item.Direction);
        Assert.Contains("played on this PC", item.Reason);
    }

    [Fact]
    public void Both_sides_played_is_a_conflict_and_moves_nothing()
    {
        _desktop.MakeSave();
        StickSync.CopyToStick(_desktop.NewEngine(), Plan(_desktop));
        StickSync.CopyToPc(_laptop.NewEngine(), Plan(_laptop));

        _laptop.Play(_laptop.Slot().Folder, seed: 3);
        StickSync.CopyToStick(_laptop.NewEngine(), Plan(_laptop));

        _desktop.Play(_desktop.Slot().Folder, seed: 4);

        var plan = Plan(_desktop);
        var item = Assert.Single(plan.Items);
        Assert.Equal(SyncDirection.Conflict, item.Direction);

        var before = Manifest.Build(_desktop.Slot().Folder);
        var outcome = StickSync.CopyToPc(_desktop.NewEngine(), plan);

        Assert.Empty(outcome.Copied);
        Assert.Single(outcome.NeedsChoice);
        Assert.Empty(before.Verify(_desktop.Slot().Folder));
    }

    [Fact]
    public void An_unlinked_save_with_the_same_name_is_a_conflict_not_an_overwrite()
    {
        _desktop.MakeSave();
        StickSync.CopyToStick(_desktop.NewEngine(), Plan(_desktop));

        var existing = _laptop.MakeSave(seed: 55); // never linked, same name
        var before = Manifest.Build(existing);

        var plan = Plan(_laptop);
        Assert.Equal(SyncDirection.Conflict, Assert.Single(plan.Items).Direction);

        StickSync.CopyToPc(_laptop.NewEngine(), plan);
        Assert.Empty(before.Verify(existing));
    }

    /// <summary>Two people's saves ride along together without anyone choosing per save.</summary>
    [Fact]
    public void Several_saves_move_in_one_go()
    {
        _desktop.MakeSave(saveName: "His Game", seed: 1);
        _desktop.MakeSave(saveName: "Her Game", seed: 2);

        var plan = Plan(_desktop);
        Assert.Equal(2, plan.Items.Count);
        Assert.All(plan.Items, i => Assert.Equal(SyncDirection.ToStick, i.Direction));

        var outcome = StickSync.CopyToStick(_desktop.NewEngine(), plan);
        Assert.Equal(2, outcome.Copied.Count);

        var laptopPlan = Plan(_laptop);
        Assert.Equal(2, laptopPlan.ToPc.Count());

        StickSync.CopyToPc(_laptop.NewEngine(), laptopPlan);
        Assert.Equal(2, SaveDiscovery.Enumerate(_laptop.Location).Count);
    }

    /// <summary>Mixed directions in one press: one save out, one save in, neither confused.</summary>
    [Fact]
    public void Directions_are_decided_per_save_not_per_stick()
    {
        _desktop.MakeSave(saveName: "His Game", seed: 1);
        StickSync.CopyToStick(_desktop.NewEngine(), Plan(_desktop));
        StickSync.CopyToPc(_laptop.NewEngine(), Plan(_laptop));

        // Laptop plays His Game and adds one of its own.
        _laptop.Play(_laptop.Slot(saveName: "His Game").Folder, seed: 12);
        _laptop.MakeSave(saveName: "Her Game", seed: 9);
        StickSync.CopyToStick(_laptop.NewEngine(), Plan(_laptop));

        var plan = Plan(_desktop);
        Assert.Equal(2, plan.ToPc.Count());
        Assert.Empty(plan.ToStick);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void The_game_running_blocks_the_whole_plan()
    {
        _desktop.MakeSave();
        try
        {
            TransferEngine.IsGameRunningProbe = () => true;
            var plan = Plan(_desktop);

            Assert.True(plan.HasBlockers);
            Assert.Throws<TransferBlockedException>(() => StickSync.CopyToStick(_desktop.NewEngine(), plan));
        }
        finally
        {
            TransferEngine.IsGameRunningProbe = () => false;
        }
    }

    [Fact]
    public void Only_the_newest_package_on_the_stick_is_considered()
    {
        _desktop.MakeSave();
        StickSync.CopyToStick(_desktop.NewEngine(), Plan(_desktop));

        _desktop.Play(_desktop.Slot().Folder, seed: 31);
        StickSync.CopyToStick(_desktop.NewEngine(), Plan(_desktop));

        // Two versions now sit on the stick; a fresh PC must take the newer one.
        Assert.Equal(2, TransferEngine.FindPackages(_stick).Count);

        var plan = Plan(_laptop);
        var item = Assert.Single(plan.Items);
        Assert.Equal(SyncDirection.ToPc, item.Direction);
        Assert.Equal(_desktop.Slot().Passport!.VersionId, item.Package!.Passport.VersionId);
    }

    // ---------------------------------------------------------------- picking individual saves

    [Fact]
    public void Only_the_chosen_saves_are_brought_over()
    {
        // Three saves on the stick, one wanted. Before this, "put the saves on this PC" was all or
        // nothing, so wanting two out of three meant taking the third as well - or none at all.
        _desktop.MakeSave(world: "Navezgane", saveName: "My Game");
        _desktop.MakeSave(world: "Navezgane", saveName: "Chris world", seed: 7);
        _desktop.MakeSave(world: "Befedite County", saveName: "Ariels World", seed: 9);

        var sender = _desktop.NewEngine();
        foreach (var slot in SaveDiscovery.Enumerate(_desktop.Location))
            sender.Export(slot, _stick);

        var engine = _laptop.NewEngine();
        var plan = StickSync.Build(engine, _stick);
        Assert.Equal(3, plan.ToPc.Count());

        var wanted = plan.ToPc.Where(i => i.SaveName == "Ariels World").ToList();
        var outcome = StickSync.CopyToPc(engine, plan, null, default, wanted);

        Assert.Equal("Ariels World (Befedite County)", Assert.Single(outcome.Copied));

        var here = SaveDiscovery.Enumerate(_laptop.Location);
        Assert.Equal("Ariels World", Assert.Single(here).SaveName);
    }

    [Fact]
    public void Passing_nothing_still_means_all_of_them()
    {
        // Every existing caller, and the single-save case, must keep working untouched.
        _desktop.MakeSave(world: "Navezgane", saveName: "One");
        _desktop.MakeSave(world: "Navezgane", saveName: "Two", seed: 7);

        var sender = _desktop.NewEngine();
        foreach (var slot in SaveDiscovery.Enumerate(_desktop.Location))
            sender.Export(slot, _stick);

        var engine = _laptop.NewEngine();
        var outcome = StickSync.CopyToPc(engine, StickSync.Build(engine, _stick));

        Assert.Equal(2, outcome.Copied.Count);
        Assert.Equal(2, SaveDiscovery.Enumerate(_laptop.Location).Count);
    }

    [Fact]
    public void Choosing_one_save_does_not_drag_in_another_that_needs_a_decision()
    {
        // The whole point: a save that needs thinking about must not block the ones that do not.
        _desktop.MakeSave(world: "Navezgane", saveName: "Shared");
        _desktop.MakeSave(world: "Navezgane", saveName: "Clean", seed: 7);

        var sender = _desktop.NewEngine();
        foreach (var slot in SaveDiscovery.Enumerate(_desktop.Location))
            sender.Export(slot, _stick);

        // The laptop already has its own unlinked "Shared" - the case that needs a human.
        _laptop.MakeSave(world: "Navezgane", saveName: "Shared", seed: 42);
        var mine = Manifest.Build(_laptop.Slot(saveName: "Shared").Folder);

        var engine = _laptop.NewEngine();
        var plan = StickSync.Build(engine, _stick);

        var clean = plan.ToPc.Concat(plan.Conflicts).Where(i => i.SaveName == "Clean").ToList();
        var outcome = StickSync.CopyToPc(engine, plan, null, default, clean);

        Assert.Contains(outcome.Copied, c => c.StartsWith("Clean"));
        Assert.DoesNotContain(outcome.Copied, c => c.StartsWith("Shared"));

        // And the one left alone is untouched, byte for byte.
        Assert.Empty(mine.Verify(_laptop.Slot(saveName: "Shared").Folder));
    }
}
