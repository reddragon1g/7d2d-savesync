using SaveSync.Core;
using Xunit;

namespace SaveSync.Core.Tests;

/// <summary>
/// Backups and crash recovery. The invariant under test throughout: a save is never deleted, only
/// moved, so every interruption leaves something restorable.
/// </summary>
public class SnapshotTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private SnapshotStore Store(int keep = 5) => new(new Workspace(_env.Location.UserDataRoot), keep);

    [Fact]
    public void Restore_brings_back_byte_identical_content()
    {
        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();
        var original = Manifest.Build(save);

        var store = Store();
        var snap = store.CaptureCopy(slot, "before a risky change");

        _env.Play(save, seed: 55);
        Assert.NotEmpty(original.Verify(save));

        store.Restore(snap, save);
        Assert.Empty(original.Verify(save));
    }

    [Fact]
    public void Restoring_also_backs_up_whatever_it_replaced()
    {
        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();

        var store = Store();
        var snap = store.CaptureCopy(slot, "checkpoint");

        _env.Play(save, seed: 66);
        var played = Manifest.Build(save);

        store.Restore(snap, save);

        // The state that was replaced by the restore is itself recoverable.
        var all = store.ListAll();
        Assert.Contains(all, s => s.Reason.Contains("restoring a backup"));
        var replaced = all.First(s => s.Reason.Contains("restoring a backup"));
        Assert.Empty(played.Verify(replaced.Folder));
    }

    [Fact]
    public void Park_then_unpark_puts_the_save_back_exactly()
    {
        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();
        var original = Manifest.Build(save);

        var store = Store();
        var parked = store.Park(save, slot.Passport!.SaveId, slot.World, slot.SaveName, "test");

        Assert.False(Directory.Exists(save));

        store.Unpark(parked);

        Assert.True(Directory.Exists(save));
        Assert.Empty(original.Verify(save));
    }

    /// <summary>
    /// Killed after the old save was parked but before the new one landed. Nothing is in place, so
    /// recovery must put the original back rather than treat it as a spent backup.
    /// </summary>
    [Fact]
    public void Interrupted_before_the_swap_restores_the_original()
    {
        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();
        var original = Manifest.Build(save);

        var store = Store();
        store.Park(save, slot.Passport!.SaveId, slot.World, slot.SaveName, "interrupted transfer");
        Assert.False(Directory.Exists(save));

        // Fresh process, as if the machine had been restarted.
        var notes = Store().RecoverInterrupted();

        Assert.True(Directory.Exists(save));
        Assert.Empty(original.Verify(save));
        Assert.Contains(notes, n => n.Contains("back after an interrupted transfer"));
    }

    /// <summary>
    /// Killed after the replacement landed but before the bookkeeping finished. The old copy must
    /// become a normal backup, and the new save must be left alone.
    /// </summary>
    [Fact]
    public void Interrupted_after_the_swap_files_the_old_copy_as_a_backup()
    {
        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();
        var oldContent = Manifest.Build(save);

        var store = Store();
        store.Park(save, slot.Passport!.SaveId, slot.World, slot.SaveName, "interrupted transfer");

        // The replacement lands, then the process dies.
        Directory.CreateDirectory(save);
        TestEnv.WriteText(Path.Combine(save, "main.ttw"), "the new save");

        var notes = Store().RecoverInterrupted();

        Assert.Equal("the new save", File.ReadAllText(Path.Combine(save, "main.ttw")));
        Assert.Contains(notes, n => n.Contains("as a backup"));

        var backup = Store().ListAll().First();
        Assert.Empty(oldContent.Verify(backup.Folder));
    }

    [Fact]
    public void Staging_leftovers_are_discarded_but_parked_saves_never_are()
    {
        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();

        var ws = new Workspace(_env.Location.UserDataRoot);
        ws.EnsureCreated();

        var junk = Path.Combine(ws.Staging, "half-written");
        TestEnv.WriteText(Path.Combine(junk, "payload.bin"), "unverified");

        var store = Store();
        store.Park(save, slot.Passport!.SaveId, slot.World, slot.SaveName, "interrupted");

        Store().RecoverInterrupted();

        Assert.False(Directory.Exists(junk), "unverified staging data is safe to discard");
        Assert.True(Directory.Exists(save), "a parked save is never discarded");
    }

    [Fact]
    public void Prune_keeps_the_limit_and_never_drops_the_newest()
    {
        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();
        var store = Store(keep: 3);

        for (int i = 0; i < 6; i++)
        {
            _env.Play(save, seed: 100 + i);
            SaveDiscovery.Measure(slot);
            store.CaptureCopy(slot, $"checkpoint {i}");
            Thread.Sleep(1100); // snapshot ids are second-resolution
        }

        var all = store.List(slot.Passport!.SaveId);
        Assert.True(all.Count <= 3, $"expected at most 3 snapshots, found {all.Count}");
        Assert.Contains(all, s => s.Reason == "checkpoint 5");
    }

    [Fact]
    public void Pinned_snapshots_survive_pruning()
    {
        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();
        var store = Store(keep: 2);

        store.CaptureCopy(slot, "the losing side of a divergence", pinned: true);
        Thread.Sleep(1100);

        for (int i = 0; i < 5; i++)
        {
            _env.Play(save, seed: 200 + i);
            SaveDiscovery.Measure(slot);
            store.CaptureCopy(slot, $"routine {i}");
            Thread.Sleep(1100);
        }

        var all = store.List(slot.Passport!.SaveId);
        Assert.Contains(all, s => s.Pinned && s.Reason.Contains("losing side"));
    }

    [Fact]
    public void Snapshots_live_on_the_same_volume_as_the_saves()
    {
        // Commit and rollback are directory moves; a different volume would silently turn them
        // into long copies and lose atomicity.
        var ws = new Workspace(_env.Location.UserDataRoot);
        Assert.True(FileOps.SameVolume(ws.Snapshots, _env.Location.SavesDir));
        Assert.True(FileOps.SameVolume(ws.Staging, _env.Location.SavesDir));
        Assert.True(PathUtil.IsUnder(ws.Root, _env.Location.UserDataRoot));
    }

    // ---------------------------------------------------------------- the backup note

    [Fact]
    public void Every_backup_carries_a_note_saying_what_it_is()
    {
        var save = _env.MakeSave(world: "Navezgane", saveName: "Weekend Run");
        var slot = _env.AdoptedSlot(saveName: "Weekend Run");

        var snap = Store().CaptureCopy(slot, "before a risky change");

        var note = Path.Combine(snap.Folder, SnapshotStore.NoteFileName);
        Assert.True(File.Exists(note), "a backup with no note is a folder nobody can identify later");

        var text = File.ReadAllText(note);
        Assert.Contains("Weekend Run", text);
        Assert.Contains("before a risky change", text);

        // The manual route has to be in there: the note is read precisely when the tool is gone.
        Assert.Contains(save, text);
        Assert.Contains("BY HAND", text);
    }

    [Fact]
    public void The_note_does_not_follow_a_backup_into_the_live_save()
    {
        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();

        var store = Store();
        var snap = store.CaptureCopy(slot, "checkpoint");
        _env.Play(save, seed: 77);
        store.Restore(snap, save);

        Assert.False(File.Exists(Path.Combine(save, SnapshotStore.NoteFileName)));
    }

    [Fact]
    public void The_note_never_travels_inside_a_package()
    {
        Assert.True(Manifest.IsExcluded(SnapshotStore.NoteFileName));
    }

    // ---------------------------------------------------------------- retention

    [Fact]
    public void Nothing_is_deleted_automatically_with_the_default_settings()
    {
        // The promise printed on the window is "nothing is ever deleted". A count-based cap made
        // that false for the backup from three transfers ago - which is the one wanted when a
        // problem is noticed late, and the only one that cannot be recreated.
        Assert.Equal(0, new AppConfig().SnapshotsToKeep);

        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();
        var store = Store(keep: 0);

        Assert.True(store.KeepsEverything);

        for (int i = 0; i < 8; i++)
        {
            _env.Play(save, seed: 300 + i);
            SaveDiscovery.Measure(slot);
            store.CaptureCopy(slot, $"routine {i}");
            Thread.Sleep(1100); // snapshot ids are second-resolution
        }

        var all = store.List(slot.Passport!.SaveId);
        Assert.Equal(8, all.Count);
        Assert.Contains(all, s => s.Reason == "routine 0");
    }

    [Fact]
    public void Restoring_pins_the_save_it_replaced()
    {
        // Somebody reaching for an older version is the likeliest person to want the newer one back
        // five minutes later, so that copy must not be a pruning candidate.
        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();

        var store = Store(keep: 2);
        var snap = store.CaptureCopy(slot, "checkpoint");

        _env.Play(save, seed: 88);
        store.Restore(snap, save);

        var all = store.List(slot.Passport!.SaveId);
        Assert.Contains(all, s => s.Pinned && s.Reason.Contains("restoring a backup"));
    }

    [Fact]
    public void A_backup_records_where_the_save_came_from()
    {
        var save = _env.MakeSave();
        var slot = _env.AdoptedSlot();

        var snap = Store().CaptureCopy(slot, "checkpoint");

        Assert.Equal(PathUtil.Normalize(save), PathUtil.Normalize(snap.OriginalFolder));
    }
}
