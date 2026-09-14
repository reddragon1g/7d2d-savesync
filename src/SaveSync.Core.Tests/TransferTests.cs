using SaveSync.Core;
using Xunit;

namespace SaveSync.Core.Tests;

/// <summary>
/// End-to-end transfers between two simulated machines over a simulated USB stick.
/// Each test maps to one of the safety invariants the design rests on.
/// </summary>
public class TransferTests : IDisposable
{
    private readonly TestEnv _desktop = new();
    private readonly TestEnv _laptop = new();
    private readonly string _stick;

    public TransferTests()
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

    private string ExportFrom(TestEnv env, string world = "Navezgane", string save = "My Game")
    {
        var engine = env.NewEngine();
        return engine.Export(env.Slot(world, save), _stick).PackageDir;
    }

    private ImportResult ImportInto(TestEnv env, string pkg, ImportChoice choice = ImportChoice.Apply)
    {
        var engine = env.NewEngine();
        return engine.Import(engine.Inspect(pkg), choice);
    }

    // -------------------------------------------------------------- the happy path

    [Fact]
    public void Save_arrives_intact_on_a_machine_that_does_not_have_it()
    {
        _desktop.MakeSave();
        var pkg = ExportFrom(_desktop);

        var engine = _laptop.NewEngine();
        var plan = engine.Inspect(pkg);

        Assert.Equal(Relation.NoLocal, plan.Relation);
        Assert.True(plan.IsOneClickSafe);

        var result = engine.Import(plan, ImportChoice.Apply);
        Assert.True(result.Applied);

        var landed = _laptop.Slot();
        Assert.Empty(plan.Manifest.Verify(landed.Folder));
        Assert.Equal(plan.Info.Passport.VersionId, landed.Passport!.VersionId);
    }

    [Fact]
    public void Round_trip_after_play_is_a_fast_forward_and_keeps_a_backup()
    {
        var save = _desktop.MakeSave();
        ImportInto(_laptop, ExportFrom(_desktop));

        // Played on the laptop while away, then brought home.
        _laptop.Play(_laptop.Slot().Folder, seed: 42);
        var pkg = ExportFrom(_laptop);

        var engine = _desktop.NewEngine();
        var plan = engine.Inspect(pkg);
        Assert.Equal(Relation.FastForward, plan.Relation);
        Assert.True(plan.IsOneClickSafe);

        var result = engine.Import(plan, ImportChoice.Apply);
        Assert.True(result.Applied);
        Assert.NotNull(result.Backup);
        Assert.Empty(plan.Manifest.Verify(save));
    }

    /// <summary>
    /// Two characters live in one world as separate .ttp files. Moving the whole folder atomically
    /// is what keeps the other person's character alive through a trip.
    /// </summary>
    [Fact]
    public void Both_players_characters_survive_a_round_trip()
    {
        var save = _desktop.MakeSave();
        var players = Directory.GetFiles(Path.Combine(save, "Player"), "*.ttp").Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(2, players.Length);

        ImportInto(_laptop, ExportFrom(_desktop));
        _laptop.Play(_laptop.Slot().Folder, seed: 9);
        ImportInto(_desktop, ExportFrom(_laptop));

        var after = Directory.GetFiles(Path.Combine(save, "Player"), "*.ttp").Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(players, after);
    }

    // -------------------------------------------------------------- refusing to lose progress

    [Fact]
    public void Older_copy_cannot_be_applied_without_an_explicit_choice()
    {
        _desktop.MakeSave();
        var oldPkg = ExportFrom(_desktop);          // v1
        ImportInto(_laptop, oldPkg);

        _desktop.Play(_desktop.Slot().Folder, seed: 5);
        ExportFrom(_desktop);                        // desktop moves to v2

        var engine = _desktop.NewEngine();
        var plan = engine.Inspect(oldPkg);

        Assert.Equal(Relation.Stale, plan.Relation);
        Assert.False(plan.IsOneClickSafe);
        Assert.True(plan.NeedsHumanChoice);
        Assert.Throws<InvalidOperationException>(() => engine.Import(plan, ImportChoice.Apply));
    }

    [Fact]
    public void Stale_import_leaves_the_newer_save_untouched()
    {
        _desktop.MakeSave();
        var oldPkg = ExportFrom(_desktop);

        _desktop.Play(_desktop.Slot().Folder, seed: 5);
        ExportFrom(_desktop);

        var newer = Manifest.Build(_desktop.Slot().Folder);
        var engine = _desktop.NewEngine();

        try { engine.Import(engine.Inspect(oldPkg), ImportChoice.Apply); }
        catch (InvalidOperationException) { }

        Assert.Empty(newer.Verify(_desktop.Slot().Folder));
    }

    [Fact]
    public void Both_sides_played_is_reported_as_diverged_and_needs_a_choice()
    {
        _desktop.MakeSave();
        ImportInto(_laptop, ExportFrom(_desktop));

        _desktop.Play(_desktop.Slot().Folder, seed: 1);
        ExportFrom(_desktop);

        _laptop.Play(_laptop.Slot().Folder, seed: 2);
        var laptopPkg = ExportFrom(_laptop);

        var engine = _desktop.NewEngine();
        var plan = engine.Inspect(laptopPkg);

        Assert.Equal(Relation.Diverged, plan.Relation);
        Assert.False(plan.IsOneClickSafe);
        Assert.Throws<InvalidOperationException>(() => engine.Import(plan, ImportChoice.Apply));
    }

    [Fact]
    public void Resolving_a_divergence_keeps_the_losing_side_as_a_pinned_backup()
    {
        _desktop.MakeSave();
        ImportInto(_laptop, ExportFrom(_desktop));

        _desktop.Play(_desktop.Slot().Folder, seed: 1);
        ExportFrom(_desktop);
        var desktopOnly = Manifest.Build(_desktop.Slot().Folder);

        _laptop.Play(_laptop.Slot().Folder, seed: 2);
        var laptopPkg = ExportFrom(_laptop);

        var engine = _desktop.NewEngine();
        var plan = engine.Inspect(laptopPkg);
        var result = engine.Import(plan, ImportChoice.TakeIncomingKeepBackup);

        Assert.True(result.Applied);
        Assert.NotNull(result.Backup);
        Assert.True(result.Backup!.Pinned, "the losing side is the only copy of that history");

        // The discarded desktop version is still on disk, intact.
        Assert.Empty(desktopOnly.Verify(result.Backup.Folder));
    }

    /// <summary>
    /// The first run on a machine that already has saves. A passport-less save must never look like
    /// an empty slot, or setup day would quietly overwrite real progress.
    /// </summary>
    [Fact]
    public void Existing_unlinked_save_is_Unregistered_not_NoLocal()
    {
        _desktop.MakeSave();
        var pkg = ExportFrom(_desktop);

        _laptop.MakeSave(seed: 77); // a different, never-linked save with the same name

        var engine = _laptop.NewEngine();
        var plan = engine.Inspect(pkg);

        Assert.Equal(Relation.Unregistered, plan.Relation);
        Assert.False(plan.IsOneClickSafe);
        Assert.True(plan.NeedsHumanChoice);
        Assert.Throws<InvalidOperationException>(() => engine.Import(plan, ImportChoice.Apply));
    }

    [Fact]
    public void Unregistered_save_is_preserved_when_the_user_takes_the_incoming_copy()
    {
        _desktop.MakeSave();
        var pkg = ExportFrom(_desktop);

        var existing = _laptop.MakeSave(seed: 77);
        var existingManifest = Manifest.Build(existing);

        var engine = _laptop.NewEngine();
        var result = engine.Import(engine.Inspect(pkg), ImportChoice.TakeIncomingKeepBackup);

        Assert.True(result.Applied);
        Assert.NotNull(result.Backup);
        Assert.Empty(existingManifest.Verify(result.Backup!.Folder));
    }

    [Fact]
    public void KeepLocal_changes_nothing()
    {
        _desktop.MakeSave();
        var pkg = ExportFrom(_desktop);

        var existing = _laptop.MakeSave(seed: 77);
        var before = Manifest.Build(existing);

        var engine = _laptop.NewEngine();
        var result = engine.Import(engine.Inspect(pkg), ImportChoice.KeepLocal);

        Assert.False(result.Applied);
        Assert.Empty(before.Verify(existing));
    }

    // -------------------------------------------------------------- integrity

    [Fact]
    public void Damaged_package_aborts_and_leaves_the_target_alone()
    {
        _desktop.MakeSave();
        ImportInto(_laptop, ExportFrom(_desktop));

        _desktop.Play(_desktop.Slot().Folder, seed: 11);
        var pkg = ExportFrom(_desktop);

        var before = Manifest.Build(_laptop.Slot().Folder);
        TestEnv.CorruptFile(Path.Combine(PackageLayout.Payload(pkg), "Region", "r.0.0.7rg"));

        var engine = _laptop.NewEngine();
        var plan = engine.Inspect(pkg);
        var ex = Assert.Throws<TransferBlockedException>(() => engine.Import(plan, ImportChoice.Apply));

        Assert.Contains("Verification failed", ex.Message);
        Assert.Contains("package itself is damaged", ex.Message);
        Assert.Empty(before.Verify(_laptop.Slot().Folder));
    }

    [Fact]
    public void Export_detects_a_bad_write_and_leaves_no_package_behind()
    {
        _desktop.MakeSave();
        var engine = _desktop.NewEngine();
        var slot = _desktop.AdoptedSlot();

        var good = engine.Export(slot, _stick);
        Assert.True(Directory.Exists(good.PackageDir));
        Assert.Empty(Json.ReadFile<Manifest>(PackageLayout.Manifest(good.PackageDir))!
            .Verify(PackageLayout.Payload(good.PackageDir)));
    }

    [Fact]
    public void Game_running_blocks_every_transfer()
    {
        _desktop.MakeSave();
        var pkg = ExportFrom(_desktop);

        try
        {
            TransferEngine.IsGameRunningProbe = () => true;

            var engine = _laptop.NewEngine();
            var plan = engine.Inspect(pkg);

            Assert.True(plan.HasBlockers);
            Assert.Contains(plan.Findings, f => f.Severity == Severity.Blocker && f.Message.Contains("running"));
            Assert.Throws<TransferBlockedException>(() => engine.Import(plan, ImportChoice.Apply));

            var exporter = _desktop.NewEngine();
            Assert.Throws<TransferBlockedException>(() => exporter.Export(_desktop.AdoptedSlot(), _stick));
        }
        finally
        {
            TransferEngine.IsGameRunningProbe = () => false;
        }
    }

    // -------------------------------------------------------------- custom worlds

    [Fact]
    public void Generated_world_travels_with_the_save_and_is_installed()
    {
        _desktop.MakeGeneratedWorld("Wasteland Valley");
        _desktop.MakeSave(world: "Wasteland Valley", saveName: "Day 1");

        var slot = _desktop.AdoptedSlot("Wasteland Valley", "Day 1");
        Assert.Equal(WorldKind.Generated, slot.WorldKind);

        var pkg = _desktop.NewEngine().Export(slot, _stick).PackageDir;
        Assert.True(Directory.Exists(PackageLayout.World(pkg)));

        var engine = _laptop.NewEngine();
        var plan = engine.Inspect(pkg);
        Assert.False(plan.HasBlockers);

        var result = engine.Import(plan, ImportChoice.Apply);
        Assert.True(result.Applied);
        Assert.True(Directory.Exists(Path.Combine(_laptop.Location.GeneratedWorldsDir, "Wasteland Valley")));
        Assert.Contains(result.Findings, f => f.Message.Contains("Installed the custom world"));
    }

    /// <summary>
    /// The classic "I copied my save and it will not load". Better to refuse than to produce a
    /// broken world.
    /// </summary>
    [Fact]
    public void Missing_generated_world_blocks_the_import()
    {
        _desktop.MakeGeneratedWorld("Wasteland Valley");
        _desktop.MakeSave(world: "Wasteland Valley", saveName: "Day 1");
        var pkg = _desktop.NewEngine().Export(_desktop.AdoptedSlot("Wasteland Valley", "Day 1"), _stick).PackageDir;

        PathUtil.DeleteTree(PackageLayout.World(pkg)); // stick was filled by hand, world left behind

        var engine = _laptop.NewEngine();
        var plan = engine.Inspect(pkg);

        Assert.True(plan.HasBlockers);
        Assert.Contains(plan.Findings, f => f.Severity == Severity.Blocker && f.Message.Contains("custom world"));
        Assert.Throws<TransferBlockedException>(() => engine.Import(plan, ImportChoice.Apply));
    }

    [Fact]
    public void Stock_world_needs_no_extra_data()
    {
        _desktop.MakeSave();
        var pkg = ExportFrom(_desktop);

        Assert.False(Directory.Exists(PackageLayout.World(pkg)));
        Assert.Equal(WorldKind.Stock, PackageInfo.Load(pkg)!.Passport.WorldKind);
    }

    // -------------------------------------------------------------- stale sticks

    [Fact]
    public void Reinstalling_a_package_already_applied_here_is_flagged()
    {
        _desktop.MakeSave();
        var pkg = ExportFrom(_desktop);

        ImportInto(_laptop, pkg);

        var engine = _laptop.NewEngine();
        var again = engine.Inspect(pkg);

        Assert.NotNull(again.PreviouslyInstalledAt);
        Assert.False(again.IsOneClickSafe);
        Assert.Contains(again.Findings, f => f.Message.Contains("already installed"));
    }

    [Fact]
    public void Export_does_not_mint_a_new_version_when_nothing_was_played()
    {
        _desktop.MakeSave();
        var engine = _desktop.NewEngine();

        var first = engine.Export(_desktop.AdoptedSlot(), _stick).Passport;
        var second = engine.Export(_desktop.Slot(), _stick).Passport;

        Assert.Equal(first.VersionId, second.VersionId);
        Assert.Equal(first.Ordinal, second.Ordinal);
    }

    [Fact]
    public void Export_mints_a_new_version_after_play()
    {
        _desktop.MakeSave();
        var engine = _desktop.NewEngine();

        var first = engine.Export(_desktop.AdoptedSlot(), _stick).Passport;
        _desktop.Play(_desktop.Slot().Folder, seed: 31);
        var second = engine.Export(_desktop.Slot(), _stick).Passport;

        Assert.NotEqual(first.VersionId, second.VersionId);
        Assert.Equal(first.Ordinal + 1, second.Ordinal);
        Assert.Contains(first.VersionId, second.Chain);
    }

    /// <summary>
    /// There is deliberately no separate "set up this save" step. Copying an unknown save gives it
    /// an identity on the spot; the safety question only matters on the receiving side, where an
    /// independently adopted save reports as Unrelated and asks rather than overwriting.
    /// </summary>
    [Fact]
    public void Unlinked_save_is_adopted_automatically_on_first_copy()
    {
        _desktop.MakeSave();
        var engine = _desktop.NewEngine();
        var slot = _desktop.Slot();

        Assert.Null(slot.Passport);

        var result = engine.Export(slot, _stick);

        Assert.False(string.IsNullOrWhiteSpace(result.Passport.SaveId));
        Assert.NotNull(_desktop.Slot().Passport);
    }

    [Fact]
    public void Saves_adopted_separately_on_two_PCs_are_reported_as_unrelated_not_merged()
    {
        _desktop.MakeSave();
        _laptop.MakeSave(seed: 91);

        var pkg = _desktop.NewEngine().Export(_desktop.Slot(), _stick).PackageDir;
        _laptop.NewEngine().Export(_laptop.Slot(), Path.Combine(_stick, "other")); // laptop adopts on its own

        var engine = _laptop.NewEngine();
        var plan = engine.Inspect(pkg);

        Assert.Equal(Relation.Unrelated, plan.Relation);
        Assert.True(plan.NeedsHumanChoice);
        Assert.Throws<InvalidOperationException>(() => engine.Import(plan, ImportChoice.Apply));
    }

    /// <summary>
    /// The regression this exists for: the receiving PC had been played but had not copied that
    /// session anywhere, so its passport still described the older version and an incoming save
    /// looked like a clean fast-forward. Applying it would have wiped the session just played.
    /// </summary>
    [Fact]
    public void Playing_without_copying_blocks_an_otherwise_safe_fast_forward()
    {
        _desktop.MakeSave();
        ImportInto(_laptop, ExportFrom(_desktop));

        // The laptop plays, and does not copy that session anywhere.
        _laptop.Play(_laptop.Slot().Folder, seed: 64);
        var laptopEvening = Manifest.Build(_laptop.Slot().Folder);

        // Meanwhile the desktop plays on and sends a strictly newer version.
        _desktop.Play(_desktop.Slot().Folder, seed: 65);
        var pkg = ExportFrom(_desktop);

        var engine = _laptop.NewEngine();
        var plan = engine.Inspect(pkg);

        Assert.Equal(Relation.FastForward, plan.Relation);
        Assert.True(plan.LocalPlayedSinceLastCopy);
        Assert.False(plan.IsOneClickSafe, "an unsaved local session must never be auto-applied over");
        Assert.True(plan.NeedsHumanChoice);
        Assert.Throws<InvalidOperationException>(() => engine.Import(plan, ImportChoice.Apply));

        Assert.Empty(laptopEvening.Verify(_laptop.Slot().Folder));
    }

    [Fact]
    public void A_freshly_installed_save_is_not_treated_as_played()
    {
        _desktop.MakeSave();
        ImportInto(_laptop, ExportFrom(_desktop));

        _desktop.Play(_desktop.Slot().Folder, seed: 12);
        var pkg = ExportFrom(_desktop);

        var plan = _laptop.NewEngine().Inspect(pkg);

        Assert.False(plan.LocalPlayedSinceLastCopy, "importing must not look like playing");
        Assert.True(plan.IsOneClickSafe);
    }

    /// <summary>
    /// A package carried home on a stick was made hours ago. The moment it is installed, the
    /// receiving PC must not believe it has been played since - which is what happened when the
    /// check compared the passport file's timestamp against the sending machine's clock.
    /// </summary>
    [Fact]
    public void A_save_installed_from_an_old_package_is_not_treated_as_played()
    {
        _desktop.MakeSave();
        var pkg = ExportFrom(_desktop);

        // Age the package, as if it were made before dinner and carried across afterwards.
        var info = PackageInfo.Load(pkg)!;
        info.CreatedAt = DateTimeOffset.UtcNow.AddHours(-6);
        info.Passport.CommittedAt = DateTimeOffset.UtcNow.AddHours(-6);
        info.Passport.LastPlayedAt = DateTimeOffset.UtcNow.AddHours(-6);
        info.Save(pkg);

        var engine = _laptop.NewEngine();
        engine.Import(engine.Inspect(pkg), ImportChoice.Apply);

        var landed = _laptop.Slot();
        Assert.False(TransferEngine.LooksChangedSinceCommit(landed),
            "a save that has only just arrived has not been played");

        // ...and the next genuinely newer copy still applies without asking.
        _desktop.Play(_desktop.Slot().Folder, seed: 44);
        var next = ExportFrom(_desktop);

        var plan = _laptop.NewEngine().Inspect(next);
        Assert.False(plan.LocalPlayedSinceLastCopy);
        Assert.True(plan.IsOneClickSafe);
    }

    /// <summary>
    /// The two PCs disagree about what time it is. Judging "played since" from a clock would get
    /// this wrong in whichever direction the skew runs.
    /// </summary>
    [Fact]
    public void Clock_skew_between_the_PCs_does_not_change_the_answer()
    {
        _desktop.MakeSave();
        var pkg = ExportFrom(_desktop);

        // Pretend the sending PC's clock is a day fast.
        var info = PackageInfo.Load(pkg)!;
        info.Passport.CommittedAt = DateTimeOffset.UtcNow.AddDays(1);
        info.Save(pkg);

        var engine = _laptop.NewEngine();
        engine.Import(engine.Inspect(pkg), ImportChoice.Apply);

        Assert.False(TransferEngine.LooksChangedSinceCommit(_laptop.Slot()));

        // Now it really is played, and that must be noticed regardless of the silly timestamp.
        _laptop.Play(_laptop.Slot().Folder, seed: 5);
        Assert.True(TransferEngine.LooksChangedSinceCommit(_laptop.Slot()));
    }

    /// <summary>
    /// A USB stick is normally FAT32 or exFAT, which round timestamps to the nearest two seconds.
    /// A save that goes out to a stick and comes back has therefore shifted slightly, and an exact
    /// comparison would call every stick transfer "played" and invent a conflict every time.
    /// </summary>
    [Fact]
    public void Coarse_stick_timestamps_do_not_look_like_play()
    {
        _desktop.MakeSave();
        var pkg = ExportFrom(_desktop);

        // What a FAT32 stick does to every file it is handed.
        foreach (var f in Manifest.EnumerateFiles(PackageLayout.Payload(pkg)))
        {
            var fi = new FileInfo(f);
            var rounded = new DateTime(fi.LastWriteTimeUtc.Ticks
                - (fi.LastWriteTimeUtc.Ticks % TimeSpan.FromSeconds(2).Ticks), DateTimeKind.Utc);
            fi.LastWriteTimeUtc = rounded;
        }

        var engine = _laptop.NewEngine();
        engine.Import(engine.Inspect(pkg), ImportChoice.Apply);

        Assert.False(TransferEngine.LooksChangedSinceCommit(_laptop.Slot()),
            "rounding on the stick is not somebody playing the game");

        // A genuine session is still far outside the tolerance and must be noticed.
        _laptop.Play(_laptop.Slot().Folder, seed: 12);
        Assert.True(TransferEngine.LooksChangedSinceCommit(_laptop.Slot()));
    }

    [Fact]
    public void Packages_are_discoverable_on_a_stick()
    {
        _desktop.MakeSave();
        ExportFrom(_desktop);

        var found = TransferEngine.FindPackages(_stick);
        Assert.Single(found);
        Assert.True(PackageLayout.IsPackage(found[0]));
    }

    [Fact]
    public void Someone_elses_save_survives_intact_even_if_it_is_deliberately_taken_over()
    {
        // Unrelated is the case where the folder about to be replaced is not an older version of
        // anything - it is a different game, quite possibly another person's. Blocking the
        // automatic path is only half of it; the other half is that the bytes still exist after
        // somebody clicks through the warning on purpose.
        _desktop.MakeSave();
        _laptop.MakeSave(seed: 91);

        var theirs = Manifest.Build(_laptop.Slot().Folder);

        var pkg = _desktop.NewEngine().Export(_desktop.Slot(), _stick).PackageDir;
        _laptop.NewEngine().Export(_laptop.Slot(), Path.Combine(_stick, "other"));

        var engine = _laptop.NewEngine();
        var plan = engine.Inspect(pkg);
        Assert.Equal(Relation.Unrelated, plan.Relation);

        var result = engine.Import(plan, ImportChoice.TakeIncomingKeepBackup);

        Assert.True(result.Applied);
        var backup = result.Backup;
        Assert.NotNull(backup);

        // Byte for byte, and pinned - it is the only copy of that history in existence.
        Assert.Empty(theirs.Verify(backup!.Folder));
        Assert.True(backup.Pinned, "the losing side of an unrelated take-over must never be prunable");
    }

    [Fact]
    public void An_unrelated_save_is_never_one_click_safe()
    {
        // IsOneClickSafe is the single gate the network receiver and the stick both use to apply
        // something with nobody watching. Unrelated must never pass it.
        _desktop.MakeSave();
        _laptop.MakeSave(seed: 91);

        var pkg = _desktop.NewEngine().Export(_desktop.Slot(), _stick).PackageDir;
        _laptop.NewEngine().Export(_laptop.Slot(), Path.Combine(_stick, "other"));

        var plan = _laptop.NewEngine().Inspect(pkg);

        Assert.False(plan.IsOneClickSafe);
        Assert.True(plan.NeedsHumanChoice);
    }

    // ---------------------------------------------------------------- game version

    [Fact]
    public void The_game_version_is_read_from_the_game_own_log()
    {
        // Real regression: this was only read from a multiplayer join record, so on a PC that had
        // only ever played single-player it came out blank - and a blank version silently disables
        // the "that save was played on a newer game version" warning entirely. Seen for real: three
        // packages made on a friend's PC all recorded no version at all.
        var logs = Path.Combine(_desktop.Location.UserDataRoot, "logs");
        Directory.CreateDirectory(logs);
        TestEnv.WriteText(Path.Combine(logs, "output_log_client__2026-09-13__16-23-49.txt"),
            "Mono path[0] = 'C:/whatever/7DaysToDie_Data/Managed'" + Environment.NewLine
            + "2026-09-13T16:23:51 0.093 INF Version: V 3.2.0, Build: Windows 64 Bit" + Environment.NewLine
            + "2026-09-13T16:23:51 0.094 INF System information:" + Environment.NewLine);

        Assert.Equal("V 3.2.0", SaveDiscovery.ReadGameVersionHint(_desktop.Location));
    }

    [Fact]
    public void A_missing_log_leaves_the_version_blank_rather_than_throwing()
    {
        // Best effort by design: no version downgrades to "no warning available", never a crash
        // and never a block on an otherwise fine transfer.
        Assert.Equal("", SaveDiscovery.ReadGameVersionHint(_desktop.Location));
    }

    // ---------------------------------------------------------------- keeping both

    [Fact]
    public void Keeping_both_installs_beside_the_existing_save_and_touches_nothing()
    {
        // Two people start a stock world and both accept the default name. Neither save is wrong
        // and neither should have to lose, so the answer is not "which one" - it is "both".
        _desktop.MakeSave(saveName: "My Game");
        _laptop.MakeSave(saveName: "My Game", seed: 77);

        var mine = Manifest.Build(_laptop.Slot(saveName: "My Game").Folder);
        var pkg = _desktop.NewEngine().Export(_desktop.Slot(saveName: "My Game"), _stick).PackageDir;

        var engine = _laptop.NewEngine();
        var plan = engine.Inspect(pkg);
        Assert.True(plan.NeedsHumanChoice);

        plan.InstallAsName = "My Game (from Ryan)";
        var result = engine.Import(plan, ImportChoice.InstallAsNewSave);

        Assert.True(result.Applied);

        // The original is untouched, byte for byte.
        Assert.Empty(mine.Verify(_laptop.Slot(saveName: "My Game").Folder));

        // And the incoming one is now a save in its own right.
        var added = _laptop.Slot(saveName: "My Game (from Ryan)");
        Assert.NotNull(added.Passport);
        Assert.Equal("My Game (from Ryan)", added.Passport!.SaveName);
    }

    [Fact]
    public void A_save_kept_alongside_gets_its_own_identity_not_a_shared_one()
    {
        // Sharing an id would make every later comparison between the two answer about the wrong
        // save - the incoming copy's history would be claimed by a save that never lived it.
        _desktop.MakeSave(saveName: "My Game");
        _laptop.MakeSave(saveName: "My Game", seed: 77);

        var pkg = _desktop.NewEngine().Export(_desktop.Slot(saveName: "My Game"), _stick).PackageDir;
        var incomingId = PackageInfo.Load(pkg)!.Passport.SaveId;

        var engine = _laptop.NewEngine();
        var plan = engine.Inspect(pkg);
        plan.InstallAsName = "Copy of My Game";
        engine.Import(plan, ImportChoice.InstallAsNewSave);

        var added = _laptop.Slot(saveName: "Copy of My Game");
        Assert.NotEqual(incomingId, added.Passport!.SaveId);
        Assert.Empty(added.Passport.Chain);
        Assert.Equal(1, added.Passport.Ordinal);
    }

    [Fact]
    public void Keeping_both_refuses_a_name_that_is_already_taken()
    {
        _desktop.MakeSave(saveName: "My Game");
        _laptop.MakeSave(saveName: "My Game", seed: 77);
        _laptop.MakeSave(saveName: "Taken", seed: 88);

        var pkg = _desktop.NewEngine().Export(_desktop.Slot(saveName: "My Game"), _stick).PackageDir;

        var engine = _laptop.NewEngine();
        var plan = engine.Inspect(pkg);
        plan.InstallAsName = "Taken";

        Assert.Throws<TransferBlockedException>(() => engine.Import(plan, ImportChoice.InstallAsNewSave));

        // And the save that was already called that is completely untouched.
        Assert.NotNull(_laptop.Slot(saveName: "Taken"));
    }

    [Fact]
    public void The_suggested_name_is_free_and_says_where_it_came_from()
    {
        _desktop.MakeSave(saveName: "My Game");
        _laptop.MakeSave(saveName: "My Game", seed: 77);

        var sender = _desktop.NewEngine();
        sender.Identity = "Chris";
        var pkg = sender.Export(_desktop.Slot(saveName: "My Game"), _stick).PackageDir;

        var plan = _laptop.NewEngine().Inspect(pkg);
        var suggested = plan.SuggestedNewName();

        Assert.Contains("Chris", suggested);
        Assert.False(Directory.Exists(Path.Combine(
            Path.GetDirectoryName(plan.TargetFolder)!, suggested)));
    }
}
