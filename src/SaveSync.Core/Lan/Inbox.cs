namespace SaveSync.Core.Lan;

public sealed class InboxItem
{
    public required string Dir { get; init; }
    public required PackageInfo Info { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }

    public string Display => $"{Info.Passport.SaveName} ({Info.Passport.World})";
}

/// <summary>
/// Where packages sent over the network land before anything is done with them.
///
/// Received data is written here first and never straight into the game's save folder, so an
/// incoming transfer is exactly as inert as a USB stick until this machine's own engine has
/// verified it and decided it is safe. The inbox sits inside the workspace, on the same volume as
/// the saves, so applying it is still an atomic move.
/// </summary>
public static class Inbox
{
    public static string Root(Workspace ws) => Path.Combine(ws.Root, "inbox");

    public static string NewSlot(Workspace ws, string token)
    {
        var dir = Path.Combine(Root(ws), PathUtil.Sanitize(token));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static List<InboxItem> List(Workspace ws)
    {
        var result = new List<InboxItem>();
        var root = Root(ws);
        if (!Directory.Exists(root)) return result;

        foreach (var dir in Directory.GetDirectories(root))
        {
            if (!PackageLayout.IsPackage(dir)) continue;
            var info = PackageInfo.Load(dir);
            if (info is null) continue;

            result.Add(new InboxItem
            {
                Dir = dir,
                Info = info,
                ReceivedAt = Directory.GetCreationTimeUtc(dir),
            });
        }

        return result.OrderByDescending(i => i.ReceivedAt).ToList();
    }

    public static void Discard(string dir)
    {
        try { PathUtil.DeleteTree(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Removes half-finished uploads and anything successfully applied long ago. An incomplete
    /// slot has no package.json, so it can never be mistaken for something worth keeping.
    /// </summary>
    public static void Cleanup(Workspace ws, TimeSpan keepFor)
    {
        var root = Root(ws);
        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                bool complete = PackageLayout.IsPackage(dir);
                var age = DateTimeOffset.UtcNow - Directory.GetCreationTimeUtc(dir);

                if (!complete && age > TimeSpan.FromHours(6)) PathUtil.DeleteTree(dir);
                else if (complete && age > keepFor) PathUtil.DeleteTree(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Resolves a path supplied by a peer to somewhere inside <paramref name="slotDir"/>.
    ///
    /// The peer controls this string, so it is treated as hostile: absolute paths, drive letters,
    /// and anything that climbs out with .. are refused outright rather than sanitised, because a
    /// silently rewritten path could still land somewhere unintended.
    /// </summary>
    public static string ResolveInside(string slotDir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidDataException("Empty file path in transfer.");

        if (relative.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException($"Refusing a path that climbs out of the folder: {relative}");

        if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException($"Refusing an absolute path in transfer: {relative}");

        var combined = Path.GetFullPath(Path.Combine(slotDir, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathUtil.IsUnder(combined, slotDir))
            throw new InvalidDataException($"Refusing a path outside the folder: {relative}");

        return combined;
    }
}
