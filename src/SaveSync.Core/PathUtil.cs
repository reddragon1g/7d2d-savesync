namespace SaveSync.Core;

public static class PathUtil
{
    /// <summary>
    /// Canonical form for comparison and display. Steam writes its registry paths with forward
    /// slashes and lowercase drive letters, so raw strings from different sources will not compare
    /// equal even when they name the same folder.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    public static bool SamePath(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="child"/> sits inside <paramref name="parent"/>.</summary>
    public static bool IsUnder(string child, string parent)
    {
        var c = Normalize(child);
        var p = Normalize(parent);
        if (c.Length <= p.Length) return false;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase)
               && (c[p.Length] == Path.DirectorySeparatorChar || c[p.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>Strips characters Windows will not accept, so a world name can become a folder name.</summary>
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(s) ? "_" : s;
    }

    public static string HumanBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    /// <summary>
    /// Deletes a tree, clearing read-only attributes first. Game saves routinely contain
    /// read-only files after being restored from a backup tool or copied off a stick.
    /// </summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        ClearReadOnly(path);
        Directory.Delete(path, recursive: true);
    }

    public static void ClearReadOnly(string path)
    {
        foreach (var f in Manifest.EnumerateFiles(path))
        {
            try
            {
                var attrs = File.GetAttributes(f);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, attrs & ~FileAttributes.ReadOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
