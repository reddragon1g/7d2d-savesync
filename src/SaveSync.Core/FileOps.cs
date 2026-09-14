namespace SaveSync.Core;

public static class FileOps
{
    /// <summary>
    /// Recursive copy that preserves last-write times.
    ///
    /// Timestamps are not cosmetic here: the UI uses newest-mtime as "when was this last played",
    /// and losing them would make a freshly restored backup look like it was played just now.
    /// </summary>
    public static void CopyTree(
        string source,
        string destination,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        source = PathUtil.Normalize(source);
        destination = PathUtil.Normalize(destination);

        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Source folder not found: {source}");

        if (PathUtil.IsUnder(destination, source))
            throw new IOException("Refusing to copy a folder into itself.");

        var files = Manifest.EnumerateFiles(source).ToList();
        long total = 0;
        foreach (var f in files)
        {
            try { total += new FileInfo(f).Length; } catch (IOException) { }
        }

        Directory.CreateDirectory(destination);

        // Recreate empty directories too - the game creates some it expects to find later.
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, rel));
        }

        long done = 0;
        int n = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, rel);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

            File.Copy(file, target, overwrite: true);

            try
            {
                var src = new FileInfo(file);
                new FileInfo(target).LastWriteTimeUtc = src.LastWriteTimeUtc;
                done += src.Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            n++;
            progress?.Report(new ScanProgress(n, files.Count, done, total, rel));
        }
    }

    /// <summary>
    /// Moves when both paths are on one volume, copies then deletes otherwise. Callers that need
    /// atomicity must check <see cref="SameVolume"/> themselves rather than rely on this.
    /// </summary>
    public static void MoveTree(string source, string destination, CancellationToken ct = default)
    {
        if (SameVolume(source, destination))
        {
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            Directory.Move(source, destination);
            return;
        }

        CopyTree(source, destination, null, ct);
        PathUtil.DeleteTree(source);
    }

    public static bool SameVolume(string a, string b)
    {
        try
        {
            var ra = Path.GetPathRoot(PathUtil.Normalize(a));
            var rb = Path.GetPathRoot(PathUtil.Normalize(b));
            return !string.IsNullOrEmpty(ra)
                   && string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    public static long TreeSize(string path)
    {
        long total = 0;
        foreach (var f in Manifest.EnumerateFiles(path))
        {
            try { total += new FileInfo(f).Length; } catch (IOException) { }
        }
        return total;
    }

    /// <summary>Free space on the volume holding <paramref name="path"/>, or -1 if it cannot be read.</summary>
    public static long FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(PathUtil.Normalize(path));
            if (string.IsNullOrEmpty(root)) return -1;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}
