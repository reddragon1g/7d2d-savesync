using System.Text;

namespace SaveSync.Core;

/// <summary>
/// What a save can be made to say about itself, read straight from the game's own files.
///
/// This exists for one situation, and it is the common one: two people both start a game in a
/// stock world and both accept the default name, so two completely unrelated saves are called
/// exactly the same thing. Until a save has been copied once it carries no identity of this tool's
/// making, so the only way to tell those apart is to ask the game's files.
/// </summary>
public sealed class SaveEvidence
{
    /// <summary>The version the game itself stamped into the save, e.g. "V 3.2.0 (b10)".</summary>
    public string GameVersion { get; init; } = "";

    /// <summary>
    /// Identifies the WORLD, not the game. Every save in Navezgane shares one; a random-gen world
    /// has its own. Two saves with different values cannot be the same game.
    /// </summary>
    public uint WorldFingerprint { get; init; }

    /// <summary>Game time in ticks. 24000 to a day.</summary>
    public long GameTimeTicks { get; init; }

    public int Day => (int)(GameTimeTicks / 24000) + 1;
    public int Hour => (int)(GameTimeTicks % 24000 / 1000);

    /// <summary>Everyone who has ever played in this save, by account id.</summary>
    public HashSet<string> PlayerIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which chunks exist. Land is explored, never un-explored.</summary>
    public HashSet<string> Regions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the world file could be read at all.</summary>
    public bool Readable { get; init; }

    /// <summary>Where this save lives, so the deeper per-character check can reach its player files.</summary>
    public string Folder { get; init; } = "";

    public string Describe()
        => Readable
            ? $"Day {Day}, {Hour:00}:00{Theme_Dot}{PlayerIds.Count} player{(PlayerIds.Count == 1 ? "" : "s")}"
              + $"{Theme_Dot}{Regions.Count} explored areas"
            : "could not be read";

    private const string Theme_Dot = "  -  ";

    /// <summary>
    /// Reads what the game left lying around. Never throws: unreadable evidence means "cannot
    /// tell", which is always a safe answer here because it leads to asking a person.
    /// </summary>
    public static SaveEvidence Read(string saveDir)
    {
        var world = ReadWorldFile(Path.Combine(saveDir, "main.ttw"));

        return new SaveEvidence
        {
            Folder = saveDir,
            Readable = world is not null,
            GameVersion = world?.Version ?? "",
            WorldFingerprint = world?.Fingerprint ?? 0,
            GameTimeTicks = world?.Ticks ?? 0,
            PlayerIds = ReadPlayerIds(Path.Combine(saveDir, "players.xml")),
            Regions = ReadRegions(Path.Combine(saveDir, "Region")),
        };
    }

    private sealed record WorldHeader(string Version, uint Fingerprint, long Ticks);

    /// <summary>
    /// main.ttw starts: "ttw\0", a format version, then a length-prefixed version string, then a
    /// run of 32-bit values. The offsets of the interesting ones are counted from the END of that
    /// string rather than hard-coded, because the string's length is what moves them.
    /// </summary>
    private static WorldHeader? ReadWorldFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var head = new byte[256];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int read = fs.Read(head, 0, head.Length);
                if (read < 64) return null;
            }

            if (head[0] != (byte)'t' || head[1] != (byte)'t' || head[2] != (byte)'w' || head[3] != 0) return null;

            int format = BitConverter.ToInt32(head, 4);

            int len = head[8];
            if (len is 0 or > 64) return null;
            var version = Encoding.ASCII.GetString(head, 9, len);

            int baseOffset = 9 + len;

            // Field positions are only known for the format this was read against. A newer save
            // format still yields its version string; the rest is left at zero, which reads as
            // "cannot tell" everywhere downstream rather than as a confident wrong answer.
            if (format != KnownFormat) return new WorldHeader(version, 0, 0);

            int fpOffset = baseOffset + (WorldFingerprintIndex * 4);
            int tickOffset = baseOffset + (GameTimeIndex * 4);
            if (tickOffset + 4 > head.Length) return new WorldHeader(version, 0, 0);

            return new WorldHeader(
                version,
                BitConverter.ToUInt32(head, fpOffset),
                BitConverter.ToUInt32(head, tickOffset));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The save format these field positions were read against (game V 3.2.0).</summary>
    public const int KnownFormat = 23;

    private const int WorldFingerprintIndex = 13;
    private const int GameTimeIndex = 14;

    private static HashSet<string> ReadPlayerIds(string playersXml)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(playersXml)) return ids;
            var text = File.ReadAllText(playersXml);

            // " userid=" with the leading space on purpose: nativeuserid is a different thing and
            // matching it instead silently compares Steam ids against account ids.
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, @"<player[^>]*?\suserid=""([^""]+)"""))
            {
                ids.Add(m.Groups[1].Value);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return ids;
    }

    private static HashSet<string> ReadRegions(string regionDir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(regionDir)) return names;
            foreach (var f in Directory.GetFiles(regionDir))
                names.Add(Path.GetFileName(f));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return names;
    }
}

/// <summary>What can be said with confidence about two saves that share a name.</summary>
public enum Kinship
{
    /// <summary>Not enough evidence. Leads to asking a person, which is always safe.</summary>
    Unknown,

    /// <summary>Proven different: one holds something the other has never held.</summary>
    DifferentGame,

    /// <summary>
    /// Nothing rules out the incoming copy being a later state of this one. Deliberately NOT
    /// "the same game": absence of contrary evidence is not proof, and the wrong answer here
    /// overwrites somebody's world.
    /// </summary>
    CouldBeTheSame,
}

public sealed record KinshipVerdict(Kinship Kind, string Headline, List<string> Reasons)
{
    public bool ProvenDifferent => Kind == Kinship.DifferentGame;
}

/// <summary>
/// Decides, from the game's own files, whether two same-named saves are actually the same game.
///
/// The test is one-directional on purpose. Progress only ever accumulates - land gets explored,
/// players join, the clock moves forward, a character learns things - so if THIS save holds
/// anything the incoming one has never held, the incoming one cannot be a later version of it.
/// That proves difference outright.
///
/// The reverse is not true and is never claimed. A save that merely looks like a subset might be an
/// ancestor, or might be a different game that happens not to have diverged yet, and there is no
/// way to tell those apart. So this reports "could be the same" and leaves the decision to a
/// person: being wrong in that direction means overwriting a world.
/// </summary>
public static class SaveKinship
{
    /// <summary>How far apart two clocks can be before it is worth mentioning, in whole days.</summary>
    private const int NotableDayGap = 2;

    /// <summary>
    /// A character file is a few tens of kilobytes and is read in full; this caps how many get
    /// opened so a heavily populated world cannot turn a comparison into a long job.
    /// </summary>
    private const int MaxCharactersCompared = 8;

    /// <summary>
    /// Everything a character file names - unlocked recipes, perks, challenges, buffs.
    ///
    /// Scraped rather than parsed: the file is the game's own binary format and no public
    /// description of it exists. That is acceptable here because of how the result is used - the
    /// only conclusion ever drawn from it is "these two are different", which sends the decision
    /// to a person. A scrape that under-reports costs a question that did not need asking; it can
    /// never cause an overwrite.
    /// </summary>
    private static HashSet<string> KnownThings(string saveFolder, string playerId)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(saveFolder, "Player", "EOS_" + playerId + ".ttp");
            if (!File.Exists(path)) return found;

            var data = File.ReadAllBytes(path);
            var run = new System.Text.StringBuilder();

            foreach (var b in data)
            {
                if (b >= 0x20 && b <= 0x7e) { run.Append((char)b); continue; }
                if (run.Length >= 6) found.Add(run.ToString());
                run.Clear();
            }
            if (run.Length >= 6) found.Add(run.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException) { }

        return found;
    }

    public static KinshipVerdict Compare(SaveEvidence local, SaveEvidence incoming)
    {
        var reasons = new List<string>();

        if (!local.Readable || !incoming.Readable)
            return new KinshipVerdict(Kinship.Unknown,
                "These two saves could not be read closely enough to tell them apart.", reasons);

        // A different world is the end of the argument.
        if (local.WorldFingerprint != 0 && incoming.WorldFingerprint != 0
            && local.WorldFingerprint != incoming.WorldFingerprint)
        {
            reasons.Add("They are not even the same world - the map data differs.");
            return new KinshipVerdict(Kinship.DifferentGame,
                "These are two completely different games that happen to share a name.", reasons);
        }

        // Everything below proves difference by finding something HERE that the incoming copy has
        // never had. None of it can be undone by playing, so none of it can be explained away.
        var missingPlayers = local.PlayerIds.Except(incoming.PlayerIds, StringComparer.OrdinalIgnoreCase).ToList();
        if (missingPlayers.Count > 0)
            reasons.Add($"{missingPlayers.Count} player{(missingPlayers.Count == 1 ? " has" : "s have")} "
                        + "played here who never appear in the other copy.");

        var missingRegions = local.Regions.Except(incoming.Regions, StringComparer.OrdinalIgnoreCase).ToList();
        if (missingRegions.Count > 0)
            reasons.Add($"{missingRegions.Count} explored area{(missingRegions.Count == 1 ? "" : "s")} "
                        + "here do not exist in the other copy.");

        if (local.GameTimeTicks > incoming.GameTimeTicks + (24000L * NotableDayGap))
            reasons.Add($"This one is on day {local.Day} and the other is on day {incoming.Day} - "
                        + "a world's clock never runs backwards.");

        // The sharp one. A character only ever gains things, so anything this PC's copy of a
        // character knows that the incoming copy has never heard of settles it outright - and it
        // settles the case the cheaper checks cannot, where one save merely looks like an early
        // version of the other because both started in the same place.
        foreach (var playerId in local.PlayerIds.Intersect(incoming.PlayerIds, StringComparer.OrdinalIgnoreCase)
                                                .Take(MaxCharactersCompared))
        {
            var here = KnownThings(local.Folder, playerId);
            var there = KnownThings(incoming.Folder, playerId);
            if (here.Count == 0 || there.Count == 0) continue;

            int onlyHere = here.Except(there).Count();
            if (onlyHere > 0)
            {
                reasons.Add($"A character on this PC has {onlyHere} things - recipes, perks, "
                            + "challenges - that the incoming copy of that same character has never had. "
                            + "A character never forgets, so it cannot be the same game.");
                break;
            }
        }

        if (reasons.Count > 0)
            return new KinshipVerdict(Kinship.DifferentGame,
                "These are two different games that happen to share a name.", reasons);

        // Nothing contradicts it. That is not the same as proof.
        var note = new List<string>
        {
            $"This PC: day {local.Day}, {local.PlayerIds.Count} player(s), {local.Regions.Count} explored areas.",
            $"Incoming: day {incoming.Day}, {incoming.PlayerIds.Count} player(s), {incoming.Regions.Count} explored areas.",
        };

        if (incoming.GameTimeTicks > local.GameTimeTicks + (24000L * NotableDayGap))
            note.Add($"The incoming copy is {(incoming.GameTimeTicks - local.GameTimeTicks) / 24000} days further on.");

        return new KinshipVerdict(Kinship.CouldBeTheSame,
            "Nothing rules out the incoming copy being a newer version of this one, but nothing "
            + "proves it either.", note);
    }
}
