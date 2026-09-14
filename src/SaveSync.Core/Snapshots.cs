namespace SaveSync.Core;

public sealed class SnapshotInfo
{
    public string Id { get; set; } = "";
    public string SaveId { get; set; } = "";
    public string World { get; set; } = "";
    public string SaveName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }

    /// <summary>Why this exists, in words a non-technical user can read back later.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Passport of the save as it was when this snapshot was taken.</summary>
    public Passport? Passport { get; set; }

    /// <summary>Pinned snapshots are never pruned. Set when the user keeps the losing side of a divergence.</summary>
    public bool Pinned { get; set; }

    /// <summary>
    /// Where this save lived when the backup was taken. Written into the note inside the folder so
    /// the backup can be put back by hand, with no tool, if it ever comes to that.
    /// </summary>
    public string OriginalFolder { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string Folder { get; set; } = "";

    public string Describe()
    {
        var when = CreatedAt.ToLocalTime().ToString("ddd d MMM, HH:mm");
        var who = Passport?.LastPlayedOn ?? "unknown PC";
        return $"{when} - {PathUtil.HumanBytes(SizeBytes)} - from {who}";
    }
}

/// <summary>Marker left inside a parked folder so an interrupted commit can be reconstructed.</summary>
internal sealed class TrashMarker
{
    public const string FileName = ".savesync-trash.json";
    public string TargetFolder { get; set; } = "";
    public string SaveId { get; set; } = "";
    public string World { get; set; } = "";
    public string SaveName { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Keeps the previous copy of every save that gets overwritten.
///
/// The rule the rest of the tool depends on: a save is never deleted, only moved. Overwriting means
/// parking the old copy and moving the new one in, both within one volume, so the window where
/// neither is in place is a few milliseconds and always recoverable.
/// </summary>
public sealed class SnapshotStore
{
    private readonly Workspace _ws;
    private readonly int _keep;

    /// <param name="keep">
    /// How many backups to keep per save. Zero - the default - means keep every one and never
    /// delete anything automatically, which is the only version of "nothing is ever deleted" that
    /// is actually true. A count-based cap silently threw away the backup from three transfers ago,
    /// which is exactly the one wanted when a problem is noticed late.
    /// </param>
    public SnapshotStore(Workspace workspace, int keep)
    {
        _ws = workspace;
        _keep = Math.Max(0, keep);
        _ws.EnsureCreated();
    }

    /// <summary>True when backups are never removed without the user asking.</summary>
    public bool KeepsEverything => _keep == 0;

    /// <summary>
    /// Moves <paramref name="folder"/> out of the way into the trash area, returning the parked
    /// path. The caller moves the replacement in and then calls <see cref="Commit"/>.
    /// </summary>
    public string Park(string folder, string saveId, string world, string saveName, string reason)
    {
        Directory.CreateDirectory(_ws.Trash);
        var parked = Path.Combine(_ws.Trash, Guid.NewGuid().ToString("N"));

        Directory.Move(folder, parked);

        Json.WriteFileAtomic(Path.Combine(parked, TrashMarker.FileName), new TrashMarker
        {
            TargetFolder = folder,
            SaveId = saveId,
            World = world,
            SaveName = saveName,
            Reason = reason,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        return parked;
    }

    /// <summary>Puts a parked folder back where it came from. Used when a commit fails part way.</summary>
    public void Unpark(string parkedFolder)
    {
        var marker = Json.ReadFile<TrashMarker>(Path.Combine(parkedFolder, TrashMarker.FileName));
        if (marker is null || string.IsNullOrWhiteSpace(marker.TargetFolder)) return;
        if (Directory.Exists(marker.TargetFolder)) return; // something already took the slot; leave it parked

        try { File.Delete(Path.Combine(parkedFolder, TrashMarker.FileName)); } catch (IOException) { }

        var parent = Path.GetDirectoryName(marker.TargetFolder);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        Directory.Move(parkedFolder, marker.TargetFolder);
    }

    /// <summary>Turns a parked folder into a retained snapshot. Called once the replacement is safely in place.</summary>
    public SnapshotInfo Commit(string parkedFolder, string reason, bool pinned = false)
    {
        var marker = Json.ReadFile<TrashMarker>(Path.Combine(parkedFolder, TrashMarker.FileName));
        var passport = Passport.Load(parkedFolder);

        var saveId = marker?.SaveId ?? passport?.SaveId ?? "unknown";
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var id = $"{stamp}_{(passport?.VersionId ?? Guid.NewGuid().ToString("N")[..8])}";

        var dir = _ws.SnapshotsFor(saveId);
        Directory.CreateDirectory(dir);

        var dest = Path.Combine(dir, id);
        if (Directory.Exists(dest)) dest += "_" + Guid.NewGuid().ToString("N")[..4];

        try { File.Delete(Path.Combine(parkedFolder, TrashMarker.FileName)); } catch (IOException) { }
        Directory.Move(parkedFolder, dest);

        long size = 0;
        int count = 0;
        foreach (var f in Manifest.EnumerateFiles(dest))
        {
            try { size += new FileInfo(f).Length; count++; } catch (IOException) { }
        }

        var info = new SnapshotInfo
        {
            Id = Path.GetFileName(dest),
            SaveId = saveId,
            World = marker?.World ?? passport?.World ?? "",
            SaveName = marker?.SaveName ?? passport?.SaveName ?? "",
            CreatedAt = DateTimeOffset.UtcNow,
            SizeBytes = size,
            FileCount = count,
            Reason = string.IsNullOrWhiteSpace(reason) ? marker?.Reason ?? "" : reason,
            Passport = passport,
            Pinned = pinned,
            OriginalFolder = marker?.TargetFolder ?? "",
            Folder = dest,
        };

        Json.WriteFileAtomic(MetaPath(saveId, info.Id), info);
        WriteNote(info);
        Prune(saveId);
        return info;
    }

    /// <summary>Snapshot an existing save without disturbing it. Used before a risky in-place change.</summary>
    public SnapshotInfo CaptureCopy(SaveSlot slot, string reason, bool pinned = false)
    {
        var saveId = slot.Passport?.SaveId ?? "unregistered";
        var dir = _ws.SnapshotsFor(saveId);
        Directory.CreateDirectory(dir);

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var id = $"{stamp}_{slot.Passport?.VersionId ?? "copy"}";
        var dest = Path.Combine(dir, id);
        if (Directory.Exists(dest)) dest += "_" + Guid.NewGuid().ToString("N")[..4];

        FileOps.CopyTree(slot.Folder, dest);

        var info = new SnapshotInfo
        {
            Id = Path.GetFileName(dest),
            SaveId = saveId,
            World = slot.World,
            SaveName = slot.SaveName,
            CreatedAt = DateTimeOffset.UtcNow,
            SizeBytes = slot.SizeBytes,
            FileCount = slot.FileCount,
            Reason = reason,
            Passport = slot.Passport,
            Pinned = pinned,
            OriginalFolder = slot.Folder,
            Folder = dest,
        };
        Json.WriteFileAtomic(MetaPath(saveId, info.Id), info);
        WriteNote(info);
        Prune(saveId);
        return info;
    }

    public List<SnapshotInfo> List(string saveId)
    {
        var dir = _ws.SnapshotsFor(saveId);
        var result = new List<SnapshotInfo>();
        if (!Directory.Exists(dir)) return result;

        foreach (var sub in Directory.GetDirectories(dir))
        {
            var id = Path.GetFileName(sub);
            var meta = Json.ReadFile<SnapshotInfo>(MetaPath(saveId, id));
            if (meta is null)
            {
                // Metadata lost but the payload is intact - still offer it rather than hide it.
                meta = new SnapshotInfo
                {
                    Id = id,
                    SaveId = saveId,
                    CreatedAt = Directory.GetCreationTimeUtc(sub),
                    Reason = "recovered",
                    Passport = Passport.Load(sub),
                };
            }
            meta.Folder = sub;
            result.Add(meta);
        }

        return result.OrderByDescending(s => s.CreatedAt).ToList();
    }

    public List<SnapshotInfo> ListAll()
    {
        var all = new List<SnapshotInfo>();
        if (!Directory.Exists(_ws.Snapshots)) return all;
        foreach (var saveDir in Directory.GetDirectories(_ws.Snapshots))
            all.AddRange(List(Path.GetFileName(saveDir)));
        return all.OrderByDescending(s => s.CreatedAt).ToList();
    }

    /// <summary>
    /// Puts a snapshot back into the live saves folder. The save currently in place is itself
    /// snapshotted first, so restore is as reversible as everything else.
    /// </summary>
    public void Restore(SnapshotInfo snapshot, string targetFolder)
    {
        if (!Directory.Exists(snapshot.Folder))
            throw new InvalidOperationException("That backup is no longer on disk.");

        string? parked = null;
        if (Directory.Exists(targetFolder))
            parked = Park(targetFolder, snapshot.SaveId, snapshot.World, snapshot.SaveName, "replaced by restoring a backup");

        try
        {
            var parent = Path.GetDirectoryName(targetFolder);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            FileOps.CopyTree(snapshot.Folder, targetFolder);

            // The note explains the backup folder; it has no business in a live save.
            try { File.Delete(Path.Combine(targetFolder, NoteFileName)); } catch (IOException) { }
        }
        catch
        {
            if (parked is not null)
            {
                try { PathUtil.DeleteTree(targetFolder); } catch (IOException) { }
                Unpark(parked);
            }
            throw;
        }

        // Pinned: the user reached for an older version, so whatever was in place is the copy most
        // likely to be wanted back in a hurry if this turns out to be the wrong one.
        if (parked is not null) Commit(parked, "replaced by restoring a backup", pinned: true);
    }

    public void Delete(SnapshotInfo snapshot)
    {
        PathUtil.DeleteTree(snapshot.Folder);
        try { File.Delete(MetaPath(snapshot.SaveId, snapshot.Id)); } catch (IOException) { }
    }

    /// <summary>
    /// Trims to the retention limit, if there is one. The newest snapshot and any pinned one always
    /// survive. With the default limit of zero this does nothing at all: backups are removed only
    /// when the user deletes one in the Backups window.
    /// </summary>
    public void Prune(string saveId)
    {
        if (_keep == 0) return;

        var all = List(saveId);
        if (all.Count <= _keep) return;

        var candidates = all.Skip(1).Where(s => !s.Pinned).ToList(); // never the newest
        int excess = all.Count - _keep;

        foreach (var s in candidates.OrderBy(s => s.CreatedAt).Take(Math.Max(0, excess)))
        {
            try { Delete(s); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Reconciles anything left behind by a crash or a kill mid-commit.
    ///
    /// A parked folder whose original location is now empty means the commit never finished, so it
    /// goes back. A parked folder whose location is occupied means the replacement landed and only
    /// the bookkeeping was lost, so it becomes a snapshot. Neither case deletes anything - that is
    /// the whole point of parking rather than removing.
    /// </summary>
    public List<string> RecoverInterrupted()
    {
        var notes = new List<string>();
        if (!Directory.Exists(_ws.Trash)) return notes;

        foreach (var parked in Directory.GetDirectories(_ws.Trash))
        {
            var marker = Json.ReadFile<TrashMarker>(Path.Combine(parked, TrashMarker.FileName));
            if (marker is null || string.IsNullOrWhiteSpace(marker.TargetFolder))
            {
                var info = Commit(parked, "recovered after an interrupted transfer");
                notes.Add($"Recovered an interrupted transfer as a backup ({info.Id}).");
                continue;
            }

            if (!Directory.Exists(marker.TargetFolder))
            {
                Unpark(parked);
                notes.Add($"Put {marker.SaveName} ({marker.World}) back after an interrupted transfer.");
            }
            else
            {
                var info = Commit(parked, marker.Reason);
                notes.Add($"Filed the previous {marker.SaveName} as a backup ({info.Id}).");
            }
        }

        // Staged payloads were never verified, so they are safe to discard.
        if (Directory.Exists(_ws.Staging))
        {
            foreach (var d in Directory.GetDirectories(_ws.Staging))
            {
                try { PathUtil.DeleteTree(d); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        return notes;
    }

    /// <summary>
    /// The plain-English note left inside every backup folder.
    ///
    /// A backup nobody can identify is not a backup. The metadata beside the folder is for the
    /// tool; this file is for a person opening the folder in Explorer years later, possibly with
    /// the tool long gone, which is precisely the moment a backup has to still make sense. It
    /// therefore also spells out the manual restore, so nothing here depends on SaveSync existing.
    /// </summary>
    public const string NoteFileName = "READ ME - how to put this back.txt";

    private void WriteNote(SnapshotInfo info)
    {
        var where = string.IsNullOrWhiteSpace(info.OriginalFolder)
            ? @"...\AppData\Roaming\7DaysToDie\Saves\" + info.World + @"\" + info.SaveName
            : info.OriginalFolder;

        var text = $@"WHAT THIS IS
============

This folder is a BACKUP of a 7 Days to Die save, made by SaveSync.
It was copied here automatically BEFORE something replaced that save, so nothing was lost.

  Save .......... {info.SaveName}   (world: {info.World})
  Backed up ..... {info.CreatedAt.ToLocalTime():ddd d MMM yyyy, HH:mm}
  Played by ..... {Who(info)}
  Why kept ...... {(string.IsNullOrWhiteSpace(info.Reason) ? "routine backup" : info.Reason)}
  Contents ...... {PathUtil.HumanBytes(info.SizeBytes)} in {info.FileCount:N0} files
  Came from ..... {where}


TO PUT IT BACK - THE EASY WAY
-----------------------------
Open SaveSync, click ""Backups"" at the bottom, click this one in the list, then click
""Put this one back"".

Whatever save is in place at that moment gets backed up too, so you can always change
your mind afterwards.


TO PUT IT BACK BY HAND
----------------------
Only needed if SaveSync is not around any more. It is an ordinary folder copy.

  1. Close 7 Days to Die.
  2. Go to:  {where}
  3. Rename that folder to something else - do not delete it - so you still have it.
  4. Copy THIS folder to exactly that path and name.
  5. Delete this text file out of the copy, and start the game.


Do not rename or edit the files inside this folder. The game reads them directly and a
part-edited save will not load.

This backup is only deleted if you delete it yourself in the Backups window.
";
        try { File.WriteAllText(Path.Combine(info.Folder, NoteFileName), text.Replace("\n", "\r\n")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Who(SnapshotInfo info)
    {
        var who = info.Passport?.LastPlayedOn;
        return string.IsNullOrWhiteSpace(who) ? "unknown" : who!.Replace('\\', ' ');
    }

    private string MetaPath(string saveId, string id)
        => Path.Combine(_ws.SnapshotsFor(saveId), id + ".meta.json");
}
