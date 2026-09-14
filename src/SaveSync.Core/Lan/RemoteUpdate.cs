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
    /// <summary>
    /// What a received program is called while it waits to be checked.
    ///
    /// A PREFIX, not a fixed name. Reusing one name meant that a single leftover copy that
    /// something else on the PC had taken a hold of - antivirus inspecting an unsigned executable
    /// is the obvious candidate - blocked every future update on that machine permanently, because
    /// the file could neither be deleted nor overwritten. Seen for real: one PC accepted an update
    /// and its neighbour refused every attempt afterwards, including a four-kilobyte one.
    /// </summary>
    public const string StagedPrefix = "incoming-update-";

    /// <summary>Kept for anything still looking for the old fixed name.</summary>
    public const string StagedName = StagedPrefix + "legacy.exe";

    /// <summary>Nothing larger is accepted. The self-contained build is about 66 MB.</summary>
    public const long MaxBytes = 200L * 1024 * 1024;

    public static Version RunningVersion =>
        typeof(RemoteUpdate).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// The version of the program FILE being offered - which is not the same thing as the version
    /// of whatever is doing the offering.
    ///
    /// Sending the sender's own version was wrong in the way that matters: a tool built from an
    /// older checkout offered "1.4.1" while handing over a 1.5.2 executable, the receiving PC
    /// correctly refused it as not newer, and the fix it was refusing was the one that would have
    /// stopped it wedging. Ask the file.
    /// </summary>
    public static Version VersionOf(string exePath)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            return Version.TryParse(info.FileVersion, out var v) ? v : new Version(0, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return new Version(0, 0);
        }
    }

    /// <summary>
    /// Clears out staged programs from previous attempts, best effort.
    ///
    /// One that cannot be deleted is stepped over rather than treated as a failure - that is the
    /// whole point of not reusing the name.
    /// </summary>
    public static void SweepOldStaged(Workspace workspace)
    {
        try
        {
            if (!Directory.Exists(workspace.Staging)) return;

            foreach (var file in Directory.GetFiles(workspace.Staging, StagedPrefix + "*.exe"))
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>Where it got to, and why it stopped. "It failed" is not a diagnosis.</summary>
    public sealed record Received(string? Path, string Reason)
    {
        public bool Ok => Path is not null;
    }

    public static async Task<string?> ReceiveAsync(
        Workspace workspace, Stream source, long declaredBytes, string declaredSha, CancellationToken ct = default)
        => (await ReceiveDetailedAsync(workspace, source, declaredBytes, declaredSha, ct).ConfigureAwait(false)).Path;

    /// <summary>
    /// As ReceiveAsync, but says what went wrong.
    ///
    /// Every failure used to surface as "did not match its checksum", which sent a real
    /// investigation in the wrong direction: a disk that was full, a folder that could not be
    /// written to, and an antivirus quarantining an unsigned executable mid-write all reported
    /// themselves as a corrupted transfer.
    /// </summary>
    public static async Task<Received> ReceiveDetailedAsync(
        Workspace workspace, Stream source, long declaredBytes, string declaredSha, CancellationToken ct = default)
    {
        if (declaredBytes <= 0 || declaredBytes > MaxBytes)
            return new Received(null, $"the offered size ({declaredBytes} bytes) is not believable");
        if (string.IsNullOrWhiteSpace(declaredSha))
            return new Received(null, "no checksum was offered with it");

        workspace.EnsureCreated();
        SweepOldStaged(workspace);

        // A fresh name every time, so nothing that is still holding yesterday's copy can stop
        // today's from arriving.
        var staged = Path.Combine(workspace.Staging, StagedPrefix + Guid.NewGuid().ToString("N")[..8] + ".exe");

        var free = FileOps.FreeSpace(workspace.Staging);
        if (free >= 0 && free < declaredBytes + (64L * 1024 * 1024))
            return new Received(null,
                $"not enough room on this PC ({PathUtil.HumanBytes(free)} free, "
                + $"needs {PathUtil.HumanBytes(declaredBytes)})");

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
                    return new Received(null,
                        $"the transfer stopped early - {declaredBytes - remaining:N0} of {declaredBytes:N0} bytes arrived");
                }
            }

            // Re-read from disk, which is the point: it proves what is actually sitting there, not
            // what was believed to have been written.
            long onDisk;
            string actual;
            try
            {
                onDisk = new FileInfo(staged).Length;
                actual = Sha256Of(staged);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                return new Received(null,
                    "the file could not be read back after being written - something else on this PC "
                    + $"took it away (antivirus is the usual culprit for an unsigned program): {e.Message}");
            }

            if (onDisk != declaredBytes)
            {
                try { File.Delete(staged); } catch (IOException) { }
                return new Received(null,
                    $"{onDisk:N0} bytes ended up on disk but {declaredBytes:N0} were sent - something "
                    + "changed the file while it was being written");
            }

            if (!string.Equals(actual, declaredSha, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(staged); } catch (IOException) { }
                return new Received(null, "the bytes that arrived do not match the checksum that was declared");
            }

            return new Received(staged, "ok");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try { File.Delete(staged); } catch (IOException) { }
            return new Received(null, $"{e.GetType().Name}: {e.Message}");
        }
    }
}
