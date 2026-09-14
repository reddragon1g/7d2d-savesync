using System.Security.Cryptography;

namespace SaveSync.Core.Lan;

/// <summary>
/// Handing the program itself from one PC to another.
///
/// This is the one capability here that is categorically different from the rest. Everything else
/// a peer can do amounts to offering a save, which the receiving machine then judges entirely on
/// its own terms - so pairing without asking anybody is defensible. Handing over something that
/// will be executed is not the same kind of favour, and pairing alone never grants it.
///
/// Three things therefore hold at once: the receiving machine must have been told, by a person
/// sitting at it, that it accepts updates; the bytes must match a checksum declared before any of
/// them were sent; and the offer must be newer than what is already there, so nobody's PC can be
/// walked backwards by a stale copy on somebody's stick.
/// </summary>
public static class RemoteUpdate
{
    /// <summary>Where a received program waits until it has been checked and put in place.</summary>
    public const string StagedName = "incoming-update.exe";

    /// <summary>Nothing larger is accepted. The self-contained build is about 66 MB.</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    public static Version RunningVersion =>
        typeof(RemoteUpdate).Assembly.GetName().Version ?? new Version(0, 0);

    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Reads the program off the wire into the workspace and checks it.
    ///
    /// Returns where it was staged, or null when it did not match - in which case the bytes are
    /// deleted rather than left lying around. A file that failed its checksum is either a broken
    /// transfer or something worse, and there is no version of "keep it just in case" that is
    /// sensible for an executable.
    /// </summary>
    public static async Task<string?> ReceiveAsync(
        Workspace workspace, Stream source, long declaredBytes, string declaredSha, CancellationToken ct = default)
    {
        if (declaredBytes <= 0 || declaredBytes > MaxBytes) return null;
        if (string.IsNullOrWhiteSpace(declaredSha)) return null;

        workspace.EnsureCreated();
        var staged = Path.Combine(workspace.Staging, StagedName);

        try { File.Delete(staged); } catch (IOException) { }

        try
        {
            await using (var file = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long remaining = declaredBytes;

                while (remaining > 0)
                {
                    int want = (int)Math.Min(buffer.Length, remaining);
                    int read = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                    if (read <= 0) break;

                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    remaining -= read;
                }

                // A short read means the sender stopped early; an incomplete program is not a program.
                if (remaining != 0)
                {
                    file.Close();
                    try { File.Delete(staged); } catch (IOException) { }
                    return null;
                }
            }

            if (!string.Equals(Sha256Of(staged), declaredSha, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(staged); } catch (IOException) { }
                return null;
            }

            return staged;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try { File.Delete(staged); } catch (IOException) { }
            return null;
        }
    }
}
