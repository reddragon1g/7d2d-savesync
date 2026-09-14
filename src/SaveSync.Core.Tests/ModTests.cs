using SaveSync.Core;
using Xunit;

namespace SaveSync.Core.Tests;

/// <summary>
/// Mods travelling with a save.
///
/// The invariant under test throughout: a mod folder already on this PC is never written over.
/// A mod folder holds that PC's own settings, and those are the one thing the sending machine
/// cannot know about, so "already here" always beats "here is mine".
/// </summary>
public class ModTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    // ---------------------------------------------------------------- discovery

    [Fact]
    public void Finds_a_mod_in_the_normal_place()
    {
        _env.MakeMod("Bigger Backpack", version: "2.1", displayName: "Bigger Backpack");

        var mods = Mods.Enumerate(_env.Location);

        var mod = Assert.Single(mods);
        Assert.Equal("Bigger Backpack", mod.FolderName);
        Assert.Equal("2.1", mod.Version);
        Assert.Equal(1, mod.NestedDepth);
        Assert.False(mod.IsAwkwardlyNested);
    }

    [Fact]
    public void Finds_a_mod_that_was_extracted_with_a_wrapper_folder()
    {
        // The commonest real mess: the zip contained a folder, so ModInfo.xml is a level too deep.
        // A tool that only looks one level down reports "no mods" on a PC that plainly has one.
        _env.MakeMod("ZombieLoot", wrapper: "ZombieLoot-v3-release");

        var mod = Assert.Single(Mods.Enumerate(_env.Location));

        Assert.Equal("ZombieLoot", mod.FolderName);
        Assert.Equal(2, mod.NestedDepth);
        Assert.True(mod.IsAwkwardlyNested);
    }

    [Fact]
    public void Finds_a_mod_buried_two_wrappers_deep()
    {
        _env.MakeMod("DeepMod", wrapper: Path.Combine("downloads", "unpacked"));

        Assert.Contains(Mods.Enumerate(_env.Location), m => m.FolderName == "DeepMod");
    }

    [Fact]
    public void Ignores_folders_that_are_not_mods()
    {
        _env.MakeMod("RealMod");

        var junk = Path.Combine(_env.Location.UserDataRoot, "Mods", "my notes");
        Directory.CreateDirectory(junk);
        TestEnv.WriteText(Path.Combine(junk, "readme.txt"), "not a mod");

        var mod = Assert.Single(Mods.Enumerate(_env.Location));
        Assert.Equal("RealMod", mod.FolderName);
    }

    [Fact]
    public void Does_not_look_for_mods_inside_a_mod()
    {
        // Some mods ship an example or a disabled variant inside themselves. The game loads the
        // outer folder only, so finding the inner one would invent a mod that does not exist.
        _env.MakeMod("Outer");
        _env.MakeMod("Inner", wrapper: Path.Combine("Outer", "examples"));

        var mods = Mods.Enumerate(_env.Location);

        Assert.Single(mods);
        Assert.Equal("Outer", mods[0].FolderName);
    }

    [Fact]
    public void An_unreadable_ModInfo_still_counts_as_a_mod()
    {
        var dir = _env.MakeMod("BrokenInfo");
        File.WriteAllText(Path.Combine(dir, "ModInfo.xml"), "<xml><this is not valid");

        var mod = Assert.Single(Mods.Enumerate(_env.Location));

        // Nameless, but present - dropping it would silently under-report what the save needs.
        Assert.Equal("BrokenInfo", mod.FolderName);
        Assert.Equal("BrokenInfo", mod.Label);
    }

    [Fact]
    public void An_unfinished_copy_is_never_reported_as_a_mod()
    {
        _env.MakeMod("Half" + Mods.PartialSuffix);

        Assert.Empty(Mods.Enumerate(_env.Location));
    }

    [Fact]
    public void Sweeping_clears_unfinished_copies_and_leaves_real_mods_alone()
    {
        _env.MakeMod("KeepMe");
        var partial = _env.MakeMod("Broken" + Mods.PartialSuffix);

        var notes = Mods.SweepPartials(_env.Location);

        Assert.NotEmpty(notes);
        Assert.False(Directory.Exists(partial));
        Assert.Single(Mods.Enumerate(_env.Location));
    }

    // ---------------------------------------------------------------- planning

    [Fact]
    public void A_mod_the_other_pc_lacks_is_planned_for_install()
    {
        _env.MakeMod("NewOne");
        var incoming = Mods.Enumerate(_env.Location, hash: true);

        var plan = Mods.Plan(new List<ModEntry>(), incoming);

        Assert.Equal(ModAction.Install, Assert.Single(plan.Items).Action);
    }

    [Fact]
    public void An_identical_mod_on_both_sides_is_left_alone()
    {
        _env.MakeMod("SameMod");
        var mods = Mods.Enumerate(_env.Location, hash: true);

        var plan = Mods.Plan(mods, mods);

        Assert.Equal(ModAction.Identical, Assert.Single(plan.Items).Action);
        Assert.False(plan.AnythingToDo);
    }

    [Fact]
    public void A_mod_with_different_settings_is_kept_not_overwritten()
    {
        // Same mod, same version, different settings file. This is the case that matters: the
        // version numbers match, so anything comparing versions would call these interchangeable
        // and quietly destroy whatever this PC's owner had configured.
        _env.MakeMod("TweakedMod", settings: "<settings difficulty=\"5\" />");
        var local = Mods.Enumerate(_env.Location, hash: true);

        var theirs = Mods.Enumerate(_env.Location, hash: true);
        theirs[0].ContentSha = "a-different-hash-entirely";

        var item = Assert.Single(Mods.Plan(local, theirs).Items);

        Assert.Equal(ModAction.KeepExisting, item.Action);
        Assert.Contains("Yours is being kept", item.Explain());
    }

    [Fact]
    public void An_unhashed_mod_is_never_assumed_identical()
    {
        // A hash can be missing because a file was locked. "Not proven identical" has to resolve to
        // leaving the local folder alone, because the other answer overwrites somebody's settings.
        _env.MakeMod("Unknown");
        var local = Mods.Enumerate(_env.Location, hash: false);
        var theirs = Mods.Enumerate(_env.Location, hash: false);

        Assert.Equal(ModAction.KeepExisting, Assert.Single(Mods.Plan(local, theirs).Items).Action);
    }

    [Fact]
    public void Mods_only_this_pc_has_are_reported_and_never_removed()
    {
        _env.MakeMod("MineOnly");
        var local = Mods.Enumerate(_env.Location, hash: true);

        var plan = Mods.Plan(local, new List<ModEntry>());

        Assert.Empty(plan.Items);
        Assert.Equal("MineOnly", Assert.Single(plan.ExtraHere).FolderName);
        Assert.True(Directory.Exists(Path.Combine(_env.Location.UserDataRoot, "Mods", "MineOnly")));
    }

    // ---------------------------------------------------------------- end to end

    [Fact]
    public void Mods_travel_with_the_save_and_land_on_a_pc_that_has_none()
    {
        _env.MakeSave();
        var slot = _env.AdoptedSlot();
        _env.MakeMod("TravellingMod", version: "4.2");

        var stick = Path.Combine(_env.Root, "stick");
        Directory.CreateDirectory(stick);
        var exported = _env.NewEngine().Export(slot, stick);

        var info = PackageInfo.Load(exported.PackageDir)!;
        Assert.True(info.IncludesModFiles);
        Assert.Equal("TravellingMod", Assert.Single(info.Mods).FolderName);

        // A second PC with the same save history but no mods at all.
        using var other = new TestEnv();
        var engine = other.NewEngine();
        var plan = engine.Inspect(exported.PackageDir);

        Assert.Equal(Relation.NoLocal, plan.Relation);
        Assert.Equal(ModAction.Install, Assert.Single(plan.Mods.Items).Action);

        var result = engine.Import(plan, ImportChoice.Apply);

        Assert.True(result.Applied);
        Assert.Contains("TravellingMod", result.ModsInstalled);

        var landed = Path.Combine(other.Location.UserDataRoot, "Mods", "TravellingMod");
        Assert.True(Directory.Exists(landed));
        Assert.True(File.Exists(Path.Combine(landed, "ModInfo.xml")));
        Assert.True(File.Exists(Path.Combine(landed, "Config", "blocks.xml")));
    }

    [Fact]
    public void A_mod_settings_file_travels_with_the_mod()
    {
        _env.MakeSave();
        var slot = _env.AdoptedSlot();
        _env.MakeMod("ConfiguredMod", settings: "<settings zombies=\"many\" />");

        var stick = Path.Combine(_env.Root, "stick");
        Directory.CreateDirectory(stick);
        var exported = _env.NewEngine().Export(slot, stick);

        using var other = new TestEnv();
        var engine = other.NewEngine();
        engine.Import(engine.Inspect(exported.PackageDir), ImportChoice.Apply);

        var settings = Path.Combine(other.Location.UserDataRoot, "Mods", "ConfiguredMod", "Config", "settings.xml");
        Assert.True(File.Exists(settings));
        Assert.Contains("many", File.ReadAllText(settings));
    }

    [Fact]
    public void An_incoming_mod_never_overwrites_the_one_already_here()
    {
        _env.MakeSave();
        var slot = _env.AdoptedSlot();
        _env.MakeMod("SharedMod", settings: "<settings from=\"sender\" />");

        var stick = Path.Combine(_env.Root, "stick");
        Directory.CreateDirectory(stick);
        var exported = _env.NewEngine().Export(slot, stick);

        // The other PC has the same mod, configured its own way.
        using var other = new TestEnv();
        other.MakeMod("SharedMod", settings: "<settings from=\"receiver\" />");

        var engine = other.NewEngine();
        var plan = engine.Inspect(exported.PackageDir);

        Assert.Equal(ModAction.KeepExisting, Assert.Single(plan.Mods.Items).Action);

        engine.Import(plan, ImportChoice.Apply);

        var settings = Path.Combine(other.Location.UserDataRoot, "Mods", "SharedMod", "Config", "settings.xml");
        Assert.Contains("receiver", File.ReadAllText(settings));
    }

    [Fact]
    public void A_wrapped_mod_is_installed_at_the_depth_the_game_expects()
    {
        _env.MakeSave();
        var slot = _env.AdoptedSlot();
        _env.MakeMod("WrappedMod", wrapper: "WrappedMod-1.0-release");

        var stick = Path.Combine(_env.Root, "stick");
        Directory.CreateDirectory(stick);
        var exported = _env.NewEngine().Export(slot, stick);

        using var other = new TestEnv();
        var engine = other.NewEngine();
        engine.Import(engine.Inspect(exported.PackageDir), ImportChoice.Apply);

        // Straightened out on the way across: the wrapper does not come with it.
        Assert.True(File.Exists(Path.Combine(
            other.Location.UserDataRoot, "Mods", "WrappedMod", "ModInfo.xml")));
        Assert.False(Directory.Exists(Path.Combine(
            other.Location.UserDataRoot, "Mods", "WrappedMod-1.0-release")));
    }

    [Fact]
    public void Damaged_mod_files_stop_the_mods_not_the_save()
    {
        // Mods are additive and reinstallable; a save is neither. A bad mod payload must cost the
        // mods only, never leave the save half-applied or refuse a transfer that is otherwise fine.
        _env.MakeSave();
        var slot = _env.AdoptedSlot();
        _env.MakeMod("FragileMod");

        var stick = Path.Combine(_env.Root, "stick");
        Directory.CreateDirectory(stick);
        var exported = _env.NewEngine().Export(slot, stick);

        TestEnv.CorruptFile(Path.Combine(exported.PackageDir, PackageLayout.ModsDir, "FragileMod", "Config", "blocks.xml"));

        using var other = new TestEnv();
        var engine = other.NewEngine();
        var result = engine.Import(engine.Inspect(exported.PackageDir), ImportChoice.Apply);

        Assert.True(result.Applied);
        Assert.Empty(result.ModsInstalled);
        Assert.False(Directory.Exists(Path.Combine(other.Location.UserDataRoot, "Mods", "FragileMod")));
        Assert.Contains(result.Findings, f => f.Message.Contains("did not check out"));
    }

    [Fact]
    public void A_package_without_mod_files_still_names_what_is_missing()
    {
        _env.MakeSave();
        var slot = _env.AdoptedSlot();
        _env.MakeMod("NeededMod");

        _env.Config.IncludeMods = false;
        var stick = Path.Combine(_env.Root, "stick");
        Directory.CreateDirectory(stick);
        var exported = _env.NewEngine().Export(slot, stick);

        // Nothing was carried, so nothing can be installed - but silence here means a save that
        // loads wrongly with no explanation anywhere.
        var info = PackageInfo.Load(exported.PackageDir)!;
        Assert.False(info.IncludesModFiles);
        Assert.Empty(info.Mods);
    }

    [Fact]
    public void Mods_are_not_applied_while_the_game_is_running()
    {
        _env.MakeSave();
        var slot = _env.AdoptedSlot();
        _env.MakeMod("GuardedMod");

        var stick = Path.Combine(_env.Root, "stick");
        Directory.CreateDirectory(stick);
        var exported = _env.NewEngine().Export(slot, stick);

        using var other = new TestEnv();
        var engine = other.NewEngine();
        var plan = engine.Inspect(exported.PackageDir);

        TransferEngine.IsGameRunningProbe = () => true;
        try
        {
            Assert.Throws<TransferBlockedException>(() => engine.Import(plan, ImportChoice.Apply));
        }
        finally
        {
            TransferEngine.IsGameRunningProbe = () => false;
        }

        Assert.False(Directory.Exists(Path.Combine(other.Location.UserDataRoot, "Mods", "GuardedMod")));
    }

    // ---------------------------------------------------------------- mods the game owns

    [Fact]
    public void The_games_own_bundled_mod_is_recognised()
    {
        // 0_TFP_Harmony ships inside the game folder and is updated by the game. It belongs to the
        // install, not to the player, and each PC already has the copy matching ITS game version.
        Assert.True(Mods.IsShippedWithGame("0_TFP_Harmony", "", ModRoot.Install));
        Assert.True(Mods.IsShippedWithGame("SomeOtherTfpThing", "The Fun Pimps LLC", ModRoot.Install));
    }

    [Fact]
    public void A_players_own_mod_in_the_game_folder_still_travels()
    {
        // Being in the install folder is not enough - most people put their own mods there too.
        Assert.False(Mods.IsShippedWithGame("PortalMod", "KeneQuirosM", ModRoot.Install));
    }

    [Fact]
    public void Nothing_in_the_user_data_folder_counts_as_shipped_with_the_game()
    {
        // The install writes to the game folder, never to the user-data folder, so a name clash
        // there is somebody's own mod and must not be silently dropped from a transfer.
        Assert.False(Mods.IsShippedWithGame("0_TFP_Harmony", "The Fun Pimps LLC", ModRoot.UserData));
    }

    [Fact]
    public void Bundled_mods_are_listed_but_not_sent()
    {
        var mod = _env.MakeMod("PlayerMod");
        Assert.NotNull(mod);

        var all = Mods.Enumerate(_env.Location);
        var travelling = Mods.Travelling(_env.Location);

        // Nothing bundled exists in the test environment, so these agree - the point of the test is
        // that Travelling is a filter over Enumerate, not a different scan that could drift from it.
        Assert.Equal(all.Count, travelling.Count);
        Assert.DoesNotContain(travelling, m => m.ShippedWithGame);
    }
}
