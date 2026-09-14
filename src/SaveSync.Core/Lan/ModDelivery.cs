using System.IO.Compression;
using System.Security.Cryptography;

namespace SaveSync.Core.Lan;

/// <summary>
/// Putting one mod onto another PC.
///
/// Mods already travel with saves, because a save played with mods misbehaves without them. This
/// is the other direction: a mod on its own, sent deliberately, with no save involved. It exists
/// because the answer to a real problem turned out to be a forty-line mod, and the machine that
/// needed it was the one nobody was sitting at - which is the same reason everything else here
/// can be driven from another house.
///
/// Sent as one archive rather than file by file. A mod is a handful of small files, and a single
/// message that either arrives whole or does not arrive at all avoids the half-installed folder
/// that a per-file protocol has to work to prevent.
///
/// The rule from everywhere else in this program holds: nothing already on that PC is overwritten
/// unless the person sending it says so explicitly, and a mod that is already there and identical
/// is left alone rather than rewritten.
/// </summary>
public static class ModDelivery
{
    /// <summary>Nothing larger is accepted. A code mod is kilobytes; this is generous.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Packs a mod folder into an archive in memory.
    ///
    /// In memory because these are small and because a temporary file is one more thing to leave
    /// behind on a machine that is already being asked to accept code.
    /// </summary>
    public static (byte[] Bytes, string Sha256, int FileCount) Pack(string folder)
    {
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);

                using var from = File.OpenRead(file);
                using var to = entry.Open();
                from.CopyTo(to);
            }
        }

        var bytes = buffer.ToArray();
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), files.Length);
    }

    public sealed record Installed(bool Ok, string Message, string? Folder = null);

    /// <summary>
    /// Reads an archive off the wire and puts it in this PC's mods folder.
    ///
    /// Installed under %APPDATA%, not beside the game: that folder needs no administrator rights
    /// and survives the game updating itself, and this has to work on a machine where nobody is
    /// available to approve anything.
    /// </summary>
    public static async Task<Installed> ReceiveAsync(
        GameLocation location, string modName, Stream source, long declaredBytes, string declaredSha,
        bool replaceExisting, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modName)) return new Installed(false, "no mod name was given");
        if (declaredBytes <= 0 || declaredBytes > MaxBytes)
            return new Installed(false, $"the offered size ({declaredBytes} bytes) is not believable");

        // A name that is not a plain folder name has no business being one.
        var safeName = modName.Trim();
        if (safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || safeName is "." or ".." || safeName.Contains('/') || safeName.Contains('\\'))
        {
            return new Installed(false, $"'{modName}' is not a usable mod folder name");
        }

        var bytes = new byte[declaredBytes];
        if (!await LanProtocol.ReadExactAsync(source, bytes, (int)declaredBytes, ct).ConfigureAwait(false))
            return new Installed(false, "the transfer stopped early");

        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, declaredSha, StringComparison.OrdinalIgnoreCase))
            return new Installed(false, "the bytes that arrived do not match the checksum that was declared");

        var modsDir = Mods.UserDataModsDir(location);
        var target = Path.Combine(modsDir, safeName);

        try
        {
            if (Directory.Exists(target) && !replaceExisting)
            {
                return new Installed(false,
                    $"'{safeName}' is already installed on this PC. Nothing was changed - "
                    + "send it again with replace if that is what you meant.");
            }

            Directory.CreateDirectory(modsDir);

            // Written beside the target and moved into place, so a transfer that fails halfway
            // never leaves a half-built mod folder for the game to try to load.
            var staging = Path.Combine(modsDir, "." + safeName + ".incoming");
            if (Directory.Exists(staging)) PathUtil.DeleteTree(staging);
            Directory.CreateDirectory(staging);

            using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;

                    var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));

                    // An archive entry naming its way out of the folder it is being unpacked into
                    // is the oldest trick there is, and this accepts archives over a network.
                    if (!destination.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar,
                                                StringComparison.OrdinalIgnoreCase))
                    {
                        PathUtil.DeleteTree(staging);
                        return new Installed(false, $"the archive tried to write outside its own folder ({entry.FullName})");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }

            if (Directory.Exists(target)) PathUtil.DeleteTree(target);
            Directory.Move(staging, target);

            ActivityLog.Write($"installed the mod '{safeName}' into {modsDir}");
            return new Installed(true, $"Installed '{safeName}'. It takes effect next time the game starts.", target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ActivityLog.Write($"could not install the mod '{safeName}'", e);
            return new Installed(false, "could not put it in place: " + e.Message);
        }
    }
}
