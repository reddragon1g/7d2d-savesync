using Microsoft.Win32;

namespace SaveSync.Core;

/// <summary>
/// Which graphics chip Windows hands a program, on a machine that has two.
///
/// This is the setting behind Settings > System > Display > Graphics. It is per executable and it
/// lives in one registry value, which makes it reachable from another machine - and on a laptop
/// whose discrete card has lost its fan, "use the weaker chip that still has cooling" stops being
/// an absurd idea and becomes a thing worth measuring.
///
/// It is not obvious which way that goes. A thermally crippled RTX 2060 held at 300 MHz still has
/// its own memory and its full bandwidth, which an integrated chip sharing system RAM does not;
/// but it is running at a seventh of its clock and the integrated one is not throttled at all.
/// The only way to know is to try it, which is the entire reason this exists.
///
/// Borrowed rather than set, like everything else here: what was there is captured first and put
/// back on request, and "never set" is restored by deleting rather than by writing a default.
/// </summary>
public static class GpuChoice
{
    public const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>Where a pending hand-back is written down, so it outlives this program.</summary>
    public static string NotePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SaveSync", "gpu-choice-restore.json");

    public enum Choice
    {
        /// <summary>No preference recorded - Windows decides, which is the default state.</summary>
        WindowsDecides,

        /// <summary>The built-in chip. Weak, but on a laptop it is cooled by the processor's fan.</summary>
        Integrated,

        /// <summary>The discrete card.</summary>
        HighPerformance,
    }

    public sealed class Note
    {
        public string ExePath { get; set; } = "";

        /// <summary>What was there before, or null when nothing was.</summary>
        public string? Previous { get; set; }

        public DateTimeOffset WrittenAt { get; set; } = DateTimeOffset.Now;
    }

    /// <summary>
    /// The raw form Windows stores.
    ///
    /// A semicolon-terminated list - "GpuPreference=2;" - which later Windows builds extend with
    /// other keys, so it is edited by replacing only the GpuPreference part and leaving anything
    /// else that happens to be in there alone.
    /// </summary>
    public static string Compose(string? existing, Choice choice)
    {
        var number = choice switch
        {
            Choice.Integrated => "1",
            Choice.HighPerformance => "2",
            _ => "0",
        };

        var parts = (existing ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
            .ToList();

        parts.Insert(0, "GpuPreference=" + number);
        return string.Join(";", parts) + ";";
    }

    public static Choice Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Choice.WindowsDecides;

        var part = raw.Split(';')
            .FirstOrDefault(p => p.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase));

        return part?.Split('=').ElementAtOrDefault(1)?.Trim() switch
        {
            "1" => Choice.Integrated,
            "2" => Choice.HighPerformance,
            _ => Choice.WindowsDecides,
        };
    }

    public static string Describe(Choice choice) => choice switch
    {
        Choice.Integrated => "the built-in chip",
        Choice.HighPerformance => "the discrete card",
        _ => "whatever Windows picks",
    };

    /// <summary>What is currently set for this executable, and the raw value behind it.</summary>
    public static (Choice Choice, string? Raw) Current(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            var raw = key?.GetValue(exePath) as string;
            return (Parse(raw), raw);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (Choice.WindowsDecides, null);
        }
    }

    /// <summary>
    /// Points the game at one chip or the other. Takes effect next time the game starts.
    ///
    /// Records what was there first, so this can be undone without anybody having to remember what
    /// the machine looked like beforehand.
    /// </summary>
    public static (bool Ok, string Message) Set(string exePath, Choice choice)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return (false, "The game executable was not found on this PC.");

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key is null) return (false, "Windows would not let this PC's graphics preference be opened.");

            var previous = key.GetValue(exePath) as string;

            // Only the first change is worth recording. Overwriting the note on a second change
            // would record a value this program itself wrote, and the way back would be lost.
            if (ReadNote() is null)
                WriteNote(new Note { ExePath = exePath, Previous = previous });

            if (choice == Choice.WindowsDecides && previous is null)
                return (true, "It was already left to Windows.");

            key.SetValue(exePath, Compose(previous, choice), RegistryValueKind.String);

            ActivityLog.Write($"pointed the game at {Describe(choice)} "
                              + $"(it was {Describe(Parse(previous))})");

            return (true, $"The game will use {Describe(choice)} next time it starts "
                          + $"(it was set to {Describe(Parse(previous))}).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (false, "Could not change it: " + e.Message);
        }
    }

    /// <summary>Puts the graphics preference back exactly as it was found.</summary>
    public static (bool Ok, string Message) GiveBack()
    {
        var note = ReadNote();
        if (note is null) return (true, "The graphics preference was never changed from here.");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null) { Forget(); return (true, "There is nothing to put back."); }

            if (note.Previous is null)
            {
                key.DeleteValue(note.ExePath, throwOnMissingValue: false);
                Forget();
                ActivityLog.Write("put the graphics preference back (it had never been set)");
                return (true, "Put back - it had never been set, so it is left to Windows again.");
            }

            key.SetValue(note.ExePath, note.Previous, RegistryValueKind.String);
            Forget();
            ActivityLog.Write("put the graphics preference back");
            return (true, $"Put back to {Describe(Parse(note.Previous))}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (false, "Could not put it back: " + e.Message);
        }
    }

    private static void WriteNote(Note note)
    {
        try { Json.WriteFileAtomic(NotePath, note); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static Note? ReadNote()
    {
        try { return File.Exists(NotePath) ? Json.ReadFile<Note>(NotePath) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Forget()
    {
        try { File.Delete(NotePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static Choice FromName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "integrated" or "igpu" or "intel" or "onboard" => Choice.Integrated,
        "discrete" or "nvidia" or "card" or "high" or "highperformance" => Choice.HighPerformance,
        _ => Choice.WindowsDecides,
    };
}
