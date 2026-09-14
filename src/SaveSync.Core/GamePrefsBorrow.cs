using Microsoft.Win32;

namespace SaveSync.Core;

/// <summary>
/// Borrowing the game's settings, and giving them back.
///
/// Starting somebody's game for them unattended needs a few of their settings changed - past the
/// spawn button, and on a machine whose graphics card is cooked, down to something it can actually
/// sustain. Every one of those settings persists: the game writes them to the registry when it
/// exits, so a change made for one launch silently becomes that person's game forever.
///
/// So nothing here is set. Things are borrowed: what was there is captured first, written down
/// where it survives this program being restarted, and handed back when the game closes. "It was
/// never set" is a real state and the common one, and it is restored by deleting rather than by
/// writing a zero, so a value that was not there before is not there afterwards either.
///
/// Two things this deliberately cannot do. It cannot invent a registry value that does not exist,
/// because the game stores preferences under a name with a hash on the end that is not derivable -
/// which is why settings are applied on the command line and only ever REMOVED here. And it never
/// hands anything back while the game is running, because the game rewrites its preferences on the
/// way out and would simply undo it.
/// </summary>
public static class GamePrefsBorrow
{
    /// <summary>Where the game keeps its preferences on Windows: Unity PlayerPrefs, so the registry.</summary>
    public const string PrefsKeyPath = @"Software\The Fun Pimps\7 Days To Die";

    /// <summary>A pending hand-back, kept on disk so it outlives the program that made it.</summary>
    public static string NotePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SaveSync", "borrowed-prefs.json");

    // ------------------------------------------------------------------ the note

    public sealed class Borrowed
    {
        /// <summary>The preference's plain name, e.g. OptionsGfxViewDistance.</summary>
        public string Name { get; set; } = "";

        /// <summary>Its registry value name, which carries a hash. Null when it was never set.</summary>
        public string? ValueName { get; set; }

        public string Kind { get; set; } = "";
        public int? Number { get; set; }
        public string? Text { get; set; }
        public string? Base64 { get; set; }

        public bool WasAbsent => ValueName is null;
    }

    public sealed class Note
    {
        public DateTimeOffset WrittenAt { get; set; } = DateTimeOffset.Now;

        /// <summary>Why these were borrowed, so a log line can say something useful.</summary>
        public string Reason { get; set; } = "";

        public List<Borrowed> Items { get; set; } = new();
    }

    // ------------------------------------------------------------------ capturing

    /// <summary>
    /// Records what these preferences are right now, before anything changes them.
    ///
    /// Names are matched by prefix because Unity stores each one as its name followed by "_h" and
    /// a hash; the hash is not something this can compute, but it does not need to - the name is
    /// enough to find it, and finding nothing is itself the answer worth recording.
    /// </summary>
    public static Note Capture(IEnumerable<string> names, string reason)
    {
        var note = new Note { Reason = reason };

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PrefsKeyPath);
            var present = key?.GetValueNames() ?? Array.Empty<string>();

            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var valueName = present.FirstOrDefault(
                    v => v.StartsWith(name + "_h", StringComparison.OrdinalIgnoreCase));

                if (valueName is null || key is null)
                {
                    note.Items.Add(new Borrowed { Name = name });
                    continue;
                }

                var item = new Borrowed
                {
                    Name = name,
                    ValueName = valueName,
                    Kind = key.GetValueKind(valueName).ToString(),
                };

                switch (key.GetValue(valueName))
                {
                    case int i: item.Number = i; break;
                    case long l: item.Number = (int)l; break;
                    case string s: item.Text = s; break;
                    case byte[] b: item.Base64 = Convert.ToBase64String(b); break;
                }

                note.Items.Add(item);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A capture that failed is recorded as empty, and nothing is borrowed on top of it.
        }

        return note;
    }

    public static void WriteDown(Note? note)
    {
        if (note is null || note.Items.Count == 0) return;

        try { Json.WriteFileAtomic(NotePath, note); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static Note? ReadNote()
    {
        try { return File.Exists(NotePath) ? Json.ReadFile<Note>(NotePath) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Forget()
    {
        try { File.Delete(NotePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ giving back

    /// <summary>
    /// Puts every borrowed preference back exactly as it was found.
    ///
    /// Refuses while the game is running rather than doing it and being quietly undone - the game
    /// writes its preferences on the way out, so anything restored underneath it is lost.
    /// </summary>
    public static (bool Ok, string Message) GiveBack(Note? note)
    {
        if (note is null || note.Items.Count == 0) return (true, "Nothing was borrowed.");

        if (GamePaths.IsGameRunning())
            return (false, "The game is running on this PC. It writes its settings when it closes, "
                           + "so this would be undone. Close it first.");

        int restored = 0, removed = 0, failed = 0;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PrefsKeyPath, writable: true);
            if (key is null) return (true, "This PC has no game settings to put back.");

            foreach (var item in note.Items)
            {
                try
                {
                    if (item.WasAbsent)
                    {
                        // It was never set. Restoring means leaving no trace, not writing a zero.
                        foreach (var v in key.GetValueNames()
                                     .Where(v => v.StartsWith(item.Name + "_h", StringComparison.OrdinalIgnoreCase)))
                        {
                            key.DeleteValue(v, throwOnMissingValue: false);
                            removed++;
                        }
                        continue;
                    }

                    object? data = item.Number is not null ? item.Number.Value
                                 : item.Text is not null ? item.Text
                                 : item.Base64 is not null ? Convert.FromBase64String(item.Base64)
                                 : null;

                    if (data is null) continue;

                    var kind = Enum.TryParse<RegistryValueKind>(item.Kind, out var k) ? k : RegistryValueKind.Unknown;
                    key.SetValue(item.ValueName!, data, kind);
                    restored++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or FormatException or ArgumentException)
                {
                    failed++;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (false, "Could not put them back: " + e.Message);
        }

        Forget();

        var what = $"Put back {restored + removed} setting(s)"
                   + (removed > 0 ? $" ({removed} of which had never been set)" : "")
                   + (failed > 0 ? $", {failed} could not be" : "") + ".";

        ActivityLog.Write($"gave back borrowed settings ({note.Reason}): {what}");
        return (failed == 0, what);
    }

    /// <summary>Gives back whatever is written down, if anything is.</summary>
    public static (bool Ok, string Message) GiveBackPending() => GiveBack(ReadNote());

    /// <summary>
    /// Waits for the game to finish, then gives everything back.
    ///
    /// Polled rather than waited on a process handle, because the process that is started is not
    /// always the one that ends up running - with EasyAntiCheat on, the executable hands over to
    /// another - and restoring while the game was still loading would put the settings back under
    /// a machine nobody is sitting at.
    /// </summary>
    public static void GiveBackWhenTheGameFinishes(Note? note, bool alreadyStarted)
    {
        if (note is null || note.Items.Count == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (!alreadyStarted)
                {
                    // "Not running yet" must not be read as "already finished": it takes minutes to
                    // start on the slower of these machines.
                    var appear = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
                    while (DateTimeOffset.UtcNow < appear && !GamePaths.IsGameRunning())
                        await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }

                var giveUp = DateTimeOffset.UtcNow + TimeSpan.FromHours(16);
                while (DateTimeOffset.UtcNow < giveUp && GamePaths.IsGameRunning())
                    await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

                // After the game has written its own preferences out, never before.
                await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                GiveBack(note);
            }
            catch (Exception e)
            {
                ActivityLog.Write("while waiting to give borrowed settings back", e);
            }
        });
    }

    /// <summary>
    /// Picks up a hand-back left behind by a copy of this program that is no longer running.
    ///
    /// Called at startup. This program is updated and restarted from another machine as a matter
    /// of routine, so a note outliving its author is the normal case, not an unusual one.
    /// </summary>
    public static void GiveBackOnStartup()
    {
        var note = ReadNote();
        if (note is null || note.Items.Count == 0) return;

        ActivityLog.Write($"settings borrowed at {note.WrittenAt:HH:mm} ({note.Reason}) are still out");

        if (GamePaths.IsGameRunning())
        {
            ActivityLog.Write("  the game is still running, so they go back when it closes");
            GiveBackWhenTheGameFinishes(note, alreadyStarted: true);
            return;
        }

        GiveBack(note);
    }

    // ------------------------------------------------------------------ reading

    /// <summary>One preference's current value as a number, or null when it has never been set.</summary>
    public static int? CurrentNumber(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PrefsKeyPath);
            if (key is null) return null;

            var valueName = key.GetValueNames()
                .FirstOrDefault(v => v.StartsWith(name + "_h", StringComparison.OrdinalIgnoreCase));
            if (valueName is null) return null;

            return key.GetValue(valueName) switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var v) => v,
                _ => null,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
