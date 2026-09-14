using System.Text;

namespace SaveSync.Core;

/// <summary>
/// The game's own log, off a machine nobody is sitting at.
///
/// Everything else here reads numbers from outside the game: frame rate, temperature, processor.
/// Those measure the RENDER thread, and it turns out they can all look healthy while the game is
/// unplayable - a steady thirty frames a second says nothing at all about a simulation that is
/// stalling, which is what stuttering and a player being flung around actually are.
///
/// The game says so itself, in its own log, and until now that log has only been readable by
/// somebody sitting at that PC. This makes it readable from here.
/// </summary>
public static class GameLog
{
    /// <summary>Lines worth pulling out of tens of megabytes of routine chatter.</summary>
    private static readonly string[] Interesting =
    {
        "ERR ", "EXC ", "WRN ", "Exception", "StackTrace", "NullReference",
        "Time between", "stall", "Stall", "hitch", "GC ", "OutOfMemory",
        "Failed", "failed", "Could not", "corrupt", "Timeout", "timed out",

        // The ones that describe a stutter rather than a crash. "blocked for 1.0s" is a full
        // second in which the game rendered nothing new, and it never appears as a low frame
        // rate average - which is exactly why it went unnoticed while the numbers looked fine.
        "blocked for", "Mod ", "[Mod", "Harmony", "Patch", "took ", "skipped",
    };

    /// <summary>Routine noise that matches the above but says nothing. Filtered back out.</summary>
    private static readonly string[] Boring =
    {
        "WRN [Steamworks.NET]", "WRN Registry: ", "failed to find", "WRN AudioSource",
    };

    public sealed class Report
    {
        public string FileName { get; init; } = "";
        public DateTimeOffset WrittenAt { get; init; }
        public long SizeBytes { get; init; }

        /// <summary>How many of each distinct problem, worst offenders first.</summary>
        public List<string> Problems { get; init; } = new();

        /// <summary>The end of the log, verbatim.</summary>
        public string Tail { get; init; } = "";

        /// <summary>
        /// How each recent session actually performed, oldest last.
        ///
        /// The machine report only ever reads the session happening right now, which on a PC
        /// nobody is sitting at is a character standing perfectly still - the easiest case there
        /// is, and not the one anybody is complaining about. Past sessions are the only place a
        /// record of somebody actually PLAYING survives.
        /// </summary>
        public List<string> Sessions { get; init; } = new();

        public string Describe()
        {
            var lines = new List<string>
            {
                $"{FileName}  ({PathUtil.HumanBytes(SizeBytes)}, last written {WrittenAt.ToLocalTime():HH:mm:ss})",
                "",
            };

            if (Sessions.Count > 0)
            {
                lines.Add("How each recent session ran:");
                lines.AddRange(Sessions.Select(x => "  " + x));
                lines.Add("");
            }

            if (Problems.Count == 0)
            {
                lines.Add("Nothing in it looks like a complaint.");
            }
            else
            {
                lines.Add("What it is complaining about:");
                lines.AddRange(Problems.Select(p => "  " + p));
            }

            lines.Add("");
            lines.Add("---- the end of the log ----");
            lines.Add(Tail);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Reads the newest game log: what it is complaining about, and how it ends.
    ///
    /// The whole file is scanned for complaints but only counted, never returned - a session's log
    /// runs to tens of megabytes and the same exception repeated forty thousand times is one fact,
    /// not forty thousand. The tail comes back verbatim because the last thing a game said before
    /// it misbehaved is usually the useful part.
    /// </summary>
    public static Report? ReadLatest(GameLocation location, int tailBytes = 24 * 1024, int sessions = 4)
    {
        try
        {
            var dir = Path.Combine(location.UserDataRoot, "logs");
            if (!Directory.Exists(dir)) return null;

            // Several sessions, not one. The session that misbehaved is rarely the session
            // somebody happens to ask about afterwards - a log is per launch, and by the time the
            // question is asked the game has usually been restarted at least once.
            var logs = new DirectoryInfo(dir)
                .GetFiles("output_log*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(Math.Max(1, sessions))
                .ToList();

            if (logs.Count == 0) return null;

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var examples = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var log in logs)
            {
                try
                {
                    using var reader = new StreamReader(
                        new FileStream(log.FullName, FileMode.Open, FileAccess.Read,
                                       FileShare.ReadWrite | FileShare.Delete));

                    while (reader.ReadLine() is { } line)
                    {
                        if (!Interesting.Any(i => line.Contains(i, StringComparison.Ordinal))) continue;
                        if (Boring.Any(b => line.Contains(b, StringComparison.Ordinal))) continue;

                        var key = Shape(line);
                        counts[key] = counts.GetValueOrDefault(key) + 1;
                        if (!examples.ContainsKey(key)) examples[key] = line.Trim();
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // One unreadable log does not stop the others being useful.
                }
            }

            var problems = counts
                .OrderByDescending(kv => kv.Value)
                .Take(30)
                .Select(kv => $"{kv.Value,7:N0}  x  {Trim(examples[kv.Key], 170)}")
                .ToList();

            var newest = logs[0];

            return new Report
            {
                FileName = $"{newest.Name}  (and {logs.Count - 1} earlier session(s))",
                WrittenAt = new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero),
                SizeBytes = logs.Sum(l => l.Length),
                Problems = problems,
                Sessions = logs.Select(Summarise).Where(x => x.Length > 0).ToList(),
                Tail = Tail(newest.FullName, tailBytes),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// One session in one line: how long, how fast, and how much was going on.
    ///
    /// Chunk and object counts are carried alongside the frame rate on purpose. They barely move
    /// while somebody stands still and change constantly while somebody walks, which is the only
    /// way to tell from here whether a session is worth anything as evidence at all.
    /// </summary>
    private static string Summarise(FileInfo log)
    {
        try
        {
            var rates = new List<double>();
            int chunksLow = int.MaxValue, chunksHigh = 0, objectsHigh = 0;

            using var reader = new StreamReader(
                new FileStream(log.FullName, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete));

            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("FPS:", StringComparison.Ordinal)) continue;

                var fps = Number(line, "FPS");
                if (fps <= 0) continue;
                rates.Add(fps);

                var chunks = (int)Number(line, "Chunks");
                if (chunks > 0)
                {
                    chunksLow = Math.Min(chunksLow, chunks);
                    chunksHigh = Math.Max(chunksHigh, chunks);
                }
                objectsHigh = Math.Max(objectsHigh, (int)Number(line, "CGO"));
            }

            if (rates.Count == 0) return $"{log.Name}: never got as far as playing";

            var sorted = rates.OrderBy(r => r).ToList();
            var moved = chunksHigh > 0 && chunksHigh - chunksLow > 8;

            return $"{log.Name[^12..^4]}  {rates.Count,3} samples  "
                   + $"typical {sorted[sorted.Count / 2],5:0.0} fps  "
                   + $"worst {sorted[0],5:0.0}  best {sorted[^1],5:0.0}  "
                   + $"chunks {(chunksLow == int.MaxValue ? 0 : chunksLow)}-{chunksHigh}  "
                   + (moved ? "<- SOMEBODY WAS MOVING" : "(stood still)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static double Number(string line, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, key + @":\s*([0-9.]+)");
        return m.Success && double.TryParse(m.Groups[1].Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>
    /// Strips the varying parts out of a line so the same complaint counts as one thing.
    ///
    /// Without this, an exception thrown every frame arrives as thousands of unique lines that
    /// differ only by their timestamp, and the one fact worth knowing - that it happens constantly
    /// - is the one thing that cannot be seen.
    /// </summary>
    private static string Shape(string line)
    {
        var sb = new StringBuilder(line.Length);
        bool lastWasDigit = false;

        foreach (var c in line)
        {
            if (char.IsDigit(c))
            {
                if (!lastWasDigit) sb.Append('#');
                lastWasDigit = true;
                continue;
            }
            lastWasDigit = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Every place a phrase appears in the recent logs, with the lines around it.
    ///
    /// The counted summary deliberately collapses identical complaints into one line, which is
    /// what makes it readable - but it also throws away the stack trace underneath, and the stack
    /// trace is the part that names which piece of code is at fault. This is how to get it back
    /// without hauling tens of megabytes across the network.
    /// </summary>
    public static string Find(GameLocation location, string phrase, int after = 12, int limit = 8,
                              int sessions = 4)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return "(nothing to look for)";

        var found = new List<string>();

        try
        {
            var dir = Path.Combine(location.UserDataRoot, "logs");
            if (!Directory.Exists(dir)) return "(this PC has no game logs)";

            var logs = new DirectoryInfo(dir)
                .GetFiles("output_log*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(Math.Max(1, sessions))
                .ToList();

            foreach (var log in logs)
            {
                if (found.Count >= limit) break;

                try
                {
                    using var reader = new StreamReader(
                        new FileStream(log.FullName, FileMode.Open, FileAccess.Read,
                                       FileShare.ReadWrite | FileShare.Delete));

                    int carry = 0;
                    var block = new List<string>();

                    while (reader.ReadLine() is { } line)
                    {
                        if (carry > 0)
                        {
                            block.Add("      " + Trim(line, 200));
                            if (--carry == 0)
                            {
                                found.Add(string.Join(Environment.NewLine, block));
                                block.Clear();
                                if (found.Count >= limit) break;
                            }
                            continue;
                        }

                        if (!line.Contains(phrase, StringComparison.OrdinalIgnoreCase)) continue;

                        block.Add($"--- {log.Name}");
                        block.Add("  " + Trim(line.Trim(), 200));
                        carry = Math.Max(1, after);
                    }

                    if (block.Count > 0) found.Add(string.Join(Environment.NewLine, block));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "(the logs could not be read)";
        }

        return found.Count == 0
            ? $"(no sign of \"{phrase}\" in the last {sessions} session(s))"
            : string.Join(Environment.NewLine + Environment.NewLine, found);
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..max] + " ...";

    private static string Tail(string path, int bytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);

            if (fs.Length > bytes) fs.Seek(-bytes, SeekOrigin.End);

            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();

            // The first line after a mid-file seek is usually half a line.
            int firstBreak = text.IndexOf('\n');
            return firstBreak >= 0 && fs.Length > bytes ? text[(firstBreak + 1)..] : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "(the log could not be read)";
        }
    }
}
