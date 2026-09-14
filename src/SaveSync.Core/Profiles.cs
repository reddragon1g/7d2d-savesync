namespace SaveSync.Core;

public sealed class Profile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }

    public override string ToString() => Name;
}

/// <summary>
/// The people who use this stick.
///
/// Stored next to the program rather than on any one PC, so the list travels with the stick and a
/// laptop shared by two people does not need separate Windows accounts to keep them straight. The
/// name is what the whole UI talks about: nobody has to know or care what their PC is called.
/// </summary>
public sealed class ProfileStore
{
    public const string FileName = "profiles.json";

    public int Schema { get; set; } = 1;
    public List<Profile> Profiles { get; set; } = new();

    /// <summary>Beside the program - which in the intended setup is the USB stick itself.</summary>
    public static string PortablePath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>Fallback for when the program sits somewhere read-only, or on a fixed drive.</summary>
    public static string LocalPath => Path.Combine(AppFolders.ConfigDir, FileName);

    public static ProfileStore Load()
    {
        var store = Json.ReadFile<ProfileStore>(PortablePath)
                    ?? Json.ReadFile<ProfileStore>(LocalPath)
                    ?? new ProfileStore();

        store.Profiles.RemoveAll(p => string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name));
        return store;
    }

    /// <summary>
    /// Writes beside the program when possible and mirrors locally, so the list survives both a
    /// write-protected stick and a lost one.
    /// </summary>
    public void Save()
    {
        bool wrotePortable = false;
        try
        {
            Json.WriteFileAtomic(PortablePath, this);
            wrotePortable = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read-only stick, or running from somewhere we may not write.
        }

        try { Json.WriteFileAtomic(LocalPath, this); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!wrotePortable) throw;
        }
    }

    public Profile? ById(string? id)
        => id is null ? null : Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public Profile? ByName(string name)
        => Profiles.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a person, or returns the existing one if that name is already there.</summary>
    public Profile Add(string name)
    {
        var clean = CleanName(name);
        if (clean.Length == 0) throw new ArgumentException("A name is needed.", nameof(name));

        var existing = ByName(clean);
        if (existing is not null) return existing;

        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = clean,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUsedAt = DateTimeOffset.UtcNow,
        };
        Profiles.Add(profile);
        return profile;
    }

    public void Remove(Profile profile) => Profiles.RemoveAll(p => p.Id == profile.Id);

    public void MarkUsed(Profile profile)
    {
        profile.LastUsedAt = DateTimeOffset.UtcNow;
        Profiles = Profiles.OrderByDescending(p => p.LastUsedAt).ToList();
    }

    /// <summary>Trims, collapses whitespace, and caps the length so a name always fits on a button.</summary>
    public static string CleanName(string name)
    {
        var collapsed = string.Join(" ", (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > 24 ? collapsed[..24] : collapsed;
    }
}
