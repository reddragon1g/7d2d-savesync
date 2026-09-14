using System.Security.Cryptography;
using System.Text;

namespace SaveSync.Core;

public sealed class ManifestEntry
{
    /// <summary>Path relative to the save root, forward-slashed so packages move between machines unchanged.</summary>
    public string Path { get; set; } = "";

    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public long MTimeUtcTicks { get; set; }
}

public readonly record struct ScanProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string Current);

public sealed record VerifyProblem(string Path, string Problem);

/// <summary>
/// The per-file inventory of a save, with a SHA-256 for every file.
///
/// This is what makes "verified" mean something. A transfer is only committed once the destination
/// has recomputed every hash and matched it, so a truncated copy or a bad USB stick fails loudly
/// instead of producing a world that corrupts three hours into the next session.
/// </summary>
public sealed class Manifest
{
    public const string FileName = "manifest.json";

    public List<ManifestEntry> Entries { get; set; } = new();

    public long TotalBytes => Entries.Sum(e => e.Size);
    public int Count => Entries.Count;

    /// <summary>Files that are metadata about the payload rather than part of it.</summary>
    public static bool IsExcluded(string relativePath)
        => string.Equals(relativePath, Passport.FileName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(relativePath, SnapshotStore.NoteFileName, StringComparison.OrdinalIgnoreCase);

    public static Manifest Build(string root, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var files = EnumerateFiles(root).ToList();
        long totalBytes = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            try { totalBytes += new FileInfo(f).Length; } catch (IOException) { }
        }

        var manifest = new Manifest();
        long bytesDone = 0;
        int done = 0;

        foreach (var full in files)
        {
            ct.ThrowIfCancellationRequested();

            var rel = ToRelative(root, full);
            if (IsExcluded(rel)) { done++; continue; }

            FileInfo fi;
            try { fi = new FileInfo(full); if (!fi.Exists) { done++; continue; } }
            catch (IOException) { done++; continue; }

            manifest.Entries.Add(new ManifestEntry
            {
                Path = rel,
                Size = fi.Length,
                Sha256 = HashFile(full, ct),
                MTimeUtcTicks = fi.LastWriteTimeUtc.Ticks,
            });

            bytesDone += fi.Length;
            done++;
            progress?.Report(new ScanProgress(done, files.Count, bytesDone, totalBytes, rel));
        }

        manifest.Entries.Sort(static (a, b) => string.CompareOrdinal(a.Path.ToLowerInvariant(), b.Path.ToLowerInvariant()));
        return manifest;
    }

    /// <summary>
    /// Deterministic digest over the whole inventory. Two saves with the same ManifestSha are
    /// byte-identical; anything else differs somewhere.
    /// </summary>
    public string ComputeSha()
    {
        var sb = new StringBuilder();
        foreach (var e in Entries.OrderBy(e => e.Path.ToLowerInvariant(), StringComparer.Ordinal))
        {
            sb.Append(e.Path.ToLowerInvariant()).Append('\0')
              .Append(e.Size).Append('\0')
              .Append(e.Sha256.ToLowerInvariant()).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// Recompute the payload under <paramref name="root"/> and report every way it fails to match.
    /// An empty list is the only thing that permits a commit.
    /// </summary>
    public List<VerifyProblem> Verify(string root, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var problems = new List<VerifyProblem>();
        var expected = Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

        long bytesDone = 0;
        int done = 0;

        foreach (var e in Entries)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.Combine(root, e.Path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(full)) { problems.Add(new VerifyProblem(e.Path, "missing")); done++; continue; }

            var fi = new FileInfo(full);
            if (fi.Length != e.Size)
            {
                problems.Add(new VerifyProblem(e.Path, $"wrong size (expected {e.Size:N0}, found {fi.Length:N0})"));
                done++;
                continue;
            }

            var actual = HashFile(full, ct);
            if (!string.Equals(actual, e.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add(new VerifyProblem(e.Path, "contents do not match"));

            bytesDone += e.Size;
            done++;
            progress?.Report(new ScanProgress(done, Entries.Count, bytesDone, TotalBytes, e.Path));
        }

        // Unexpected extras matter: a stray file from a previous half-finished write can change
        // what the game loads.
        foreach (var full in EnumerateFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            var rel = ToRelative(root, full);
            if (IsExcluded(rel)) continue;
            if (!expected.ContainsKey(rel)) problems.Add(new VerifyProblem(rel, "unexpected extra file"));
        }

        return problems;
    }

    /// <summary>
    /// Entries in <paramref name="wanted"/> that the destination does not already have byte-identical.
    /// Drives delta transfer over the LAN, where unchanged Region files dominate the payload.
    /// </summary>
    public static List<ManifestEntry> Diff(Manifest wanted, Manifest destination)
    {
        var have = destination.Entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var need = new List<ManifestEntry>();
        foreach (var e in wanted.Entries)
        {
            if (have.TryGetValue(e.Path, out var d)
                && d.Size == e.Size
                && string.Equals(d.Sha256, e.Sha256, StringComparison.OrdinalIgnoreCase))
                continue;
            need.Add(e);
        }
        return need;
    }

    public static string HashFile(string path, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public static IEnumerable<string> EnumerateFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var f in Directory.EnumerateFiles(root, "*", opts)) yield return f;
    }

    public static string ToRelative(string root, string full)
        => Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
}
