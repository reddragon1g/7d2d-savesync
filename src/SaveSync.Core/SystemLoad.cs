using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SaveSync.Core;

/// <summary>
/// What a PC is actually doing right now, measured rather than inferred.
///
/// "It runs badly" has three plausible explanations and they call for completely different
/// answers: the machine is short of memory and paging, something else is eating the processor, or
/// the game is drawing on the wrong graphics chip. Guessing between them from another house is how
/// an afternoon disappears. Every number here exists to rule one of them in or out.
///
/// Nothing here may throw. It is called while answering a request from another machine, and a
/// diagnostic that can take the program down with it is worse than no diagnostic.
/// </summary>
public static class SystemLoad
{
    /// <summary>How one process is loading the machine, over a measured window.</summary>
    public sealed class ProcessLoad
    {
        public string Name { get; init; } = "";
        public int Id { get; init; }

        /// <summary>Share of ONE machine's worth of processor, so 100 means every core saturated.</summary>
        public double CpuPercent { get; init; }

        public long WorkingSetBytes { get; init; }
        public bool Essential { get; init; }

        public string Describe()
            => $"{Name,-26} {CpuPercent,5:0.0}% cpu   {PathUtil.HumanBytes(WorkingSetBytes),-10}"
               + (Essential ? " (part of Windows)" : "");
    }

    public sealed class Sample
    {
        /// <summary>Whole-machine processor use across the window. -1 when it could not be read.</summary>
        public double CpuPercent { get; init; } = -1;

        public List<ProcessLoad> Busiest { get; init; } = new();

        /// <summary>The game's own load, whether or not it was among the busiest.</summary>
        public ProcessLoad? Game { get; init; }
    }

    // ------------------------------------------------------------------ processor

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    /// <summary>
    /// Measures the machine and its processes over one window.
    ///
    /// One window for everything, deliberately: taking separate readings would mean sleeping
    /// several times inside a request another PC is waiting on, and would compare numbers gathered
    /// at different moments as though they belonged together.
    /// </summary>
    public static Sample Measure(TimeSpan window, int busiest = 8)
    {
        var start = SnapshotProcesses();
        bool haveSystem = GetSystemTimes(out var idle0, out var kernel0, out var user0);

        Thread.Sleep(window);

        bool haveSystem2 = GetSystemTimes(out var idle1, out var kernel1, out var user1);
        var end = SnapshotProcesses();

        double cpu = -1;
        if (haveSystem && haveSystem2)
        {
            // Kernel time INCLUDES idle on Windows, so total is kernel + user and busy is what is
            // left after taking idle out of it.
            double idleDelta = idle1.Value - idle0.Value;
            double total = (kernel1.Value - kernel0.Value) + (user1.Value - user0.Value);
            if (total > 0) cpu = Math.Clamp((1.0 - idleDelta / total) * 100.0, 0, 100);
        }

        var loads = new List<ProcessLoad>();
        double seconds = window.TotalSeconds <= 0 ? 1 : window.TotalSeconds;
        double cores = Math.Max(1, Environment.ProcessorCount);

        foreach (var (id, after) in end)
        {
            if (!start.TryGetValue(id, out var before)) continue;      // started mid-window
            if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal)) continue;

            var used = (after.Cpu - before.Cpu).TotalSeconds;
            if (used < 0) continue;

            loads.Add(new ProcessLoad
            {
                Name = after.Name,
                Id = id,
                CpuPercent = used / seconds / cores * 100.0,
                WorkingSetBytes = after.WorkingSet,
                Essential = ProcessUse.IsEssential(after.Name),
            });
        }

        return new Sample
        {
            CpuPercent = cpu,
            Busiest = loads.OrderByDescending(p => p.CpuPercent).Take(busiest).ToList(),
            Game = loads.FirstOrDefault(
                p => p.Name.Equals(GamePaths.ProcessName, StringComparison.OrdinalIgnoreCase)),
        };
    }

    private readonly record struct Snap(string Name, TimeSpan Cpu, long WorkingSet);

    private static Dictionary<int, Snap> SnapshotProcesses()
    {
        var map = new Dictionary<int, Snap>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { map[p.Id] = new Snap(p.ProcessName, p.TotalProcessorTime, p.WorkingSet64); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
                {
                    // Access denied for protected processes, or it exited. Neither is our business.
                }
                finally { p.Dispose(); }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }

        return map;
    }

    // ------------------------------------------------------------------ graphics

    /// <summary>
    /// What the NVIDIA card is doing, and which programs are on it.
    ///
    /// The second part is the one that matters on a laptop. A machine with an Intel chip and a
    /// discrete card can run the game on either, and the difference is enormous - but from the
    /// outside both look like "the game is slow". If the game is not in this list, it is not on
    /// the card, and no amount of tuning settings will fix that.
    /// </summary>
    public sealed class NvidiaState
    {
        public string Name { get; init; } = "";
        public int UtilisationPercent { get; init; } = -1;
        public long MemoryUsedMb { get; init; }
        public long MemoryTotalMb { get; init; }
        public int TemperatureC { get; init; } = -1;

        /// <summary>
        /// Clock speed now against the most it can do.
        ///
        /// The number that separates "this card is too slow" from "this card is being held back".
        /// A card reported as 100% busy is only saturated at whatever speed it is currently allowed
        /// to run, and a hot or power-limited laptop card runs at a fraction of its rated clock
        /// while still reading as fully occupied. Those two look identical from the outside and
        /// have completely different answers.
        /// </summary>
        public int ClockMhz { get; init; } = -1;
        public int ClockMaxMhz { get; init; } = -1;

        public double PowerWatts { get; init; } = -1;
        public double PowerLimitWatts { get; init; } = -1;

        /// <summary>What the driver says is holding it back, in its own words.</summary>
        public List<string> Throttling { get; init; } = new();

        /// <summary>
        /// The temperature the card starts backing off at, and the ones it panics at.
        ///
        /// A temperature on its own means nothing - 82C is unremarkable on one card and the edge of
        /// a cliff on another. What makes it readable is the card's own target: once it reaches
        /// that, it trades clock speed for heat, quietly, and keeps doing it. Without the threshold
        /// beside it, a hot card looks merely warm.
        /// </summary>
        public int TargetTempC { get; init; } = -1;
        public int SlowdownTempC { get; init; } = -1;
        public int ShutdownTempC { get; init; } = -1;
        public int MaxOperatingTempC { get; init; } = -1;
        public int MemoryTempC { get; init; } = -1;

        /// <summary>
        /// Fan speed as the driver reports it. -1 when it will not say.
        ///
        /// A laptop's graphics fan is often driven by the machine's embedded controller rather
        /// than by the card, and in that case the driver genuinely does not know - so "not
        /// reported" has to stay distinct from "zero", because reading the first as the second
        /// would convict a working fan on no evidence.
        /// </summary>
        public int FanPercent { get; init; } = -1;

        public string? Heat()
        {
            if (TemperatureC < 0) return null;

            var parts = new List<string> { $"GPU {TemperatureC}C" };
            if (MemoryTempC >= 0) parts.Add($"memory {MemoryTempC}C");
            if (TargetTempC > 0) parts.Add($"backs off from {TargetTempC}C");
            if (MaxOperatingTempC > 0) parts.Add($"rated to {MaxOperatingTempC}C");
            if (SlowdownTempC > 0) parts.Add($"forced slowdown at {SlowdownTempC}C");
            if (ShutdownTempC > 0) parts.Add($"shuts down at {ShutdownTempC}C");

            var line = "Temperatures: " + string.Join(", ", parts);

            line += Environment.NewLine + "  Fan: " + FanPercent switch
            {
                < 0 => "the driver will not say (common on laptops, where the machine drives the "
                       + "fan rather than the card) - judge it by how fast it cools instead",
                0 => "reported as NOT SPINNING",
                _ => $"reported as spinning at {FanPercent}%",
            };

            if (FanPercent == 0 && TemperatureC >= 70)
                line += Environment.NewLine
                        + $"  >> A fan at 0% while the card sits at {TemperatureC}C is not a fan "
                        + "idling. Nothing is moving air over it.";

            if (TargetTempC > 0 && TemperatureC >= TargetTempC - 2)
                line += Environment.NewLine
                        + $"  >> It is sitting AT its own limit ({TemperatureC}C against a {TargetTempC}C "
                        + "target), which is the card choosing to run slower rather than get hotter. "
                        + "That is a cooling problem, not a settings problem.";

            return line;
        }

        /// <summary>Programs currently on the card, by executable name.</summary>
        public List<string> Programs { get; init; } = new();

        public bool GameIsOnIt => Programs.Any(
            p => p.Contains(GamePaths.ProcessName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The card's own state. Deliberately says nothing about the game.
        ///
        /// Whether the game being absent from this card means anything at all depends on whether
        /// the game is running, which this does not know - and "the game is NOT on it" printed
        /// about a PC with the game closed is a false alarm waiting to be acted on.
        /// </summary>
        public string Describe()
        {
            var text = $"{Name}: {(UtilisationPercent < 0 ? "?" : UtilisationPercent + "%")} busy, "
                       + $"{MemoryUsedMb:N0} of {MemoryTotalMb:N0} MB"
                       + (TemperatureC < 0 ? "" : $", {TemperatureC}C");

            if (ClockMhz >= 0 && ClockMaxMhz > 0)
                text += $", running at {ClockMhz} of {ClockMaxMhz} MHz "
                        + $"({ClockMhz * 100.0 / ClockMaxMhz:0}% of its clock)";

            if (PowerWatts >= 0 && PowerLimitWatts > 0)
                text += $", drawing {PowerWatts:0} of {PowerLimitWatts:0} W";

            return text;
        }

        /// <summary>Whether the card is being held back, and by what. Null when it is running free.</summary>
        public string? HeldBack()
        {
            if (Throttling.Count > 0)
                return "The card is being HELD BACK: " + string.Join(", ", Throttling) + ".";

            if (ClockMhz > 0 && ClockMaxMhz > 0 && UtilisationPercent >= 90
                && ClockMhz < ClockMaxMhz * 0.6)
            {
                return $"The card is at {UtilisationPercent}% but only {ClockMhz} of {ClockMaxMhz} MHz. "
                       + "Fully occupied at a fraction of its speed is what heat, a power limit or a "
                       + "battery-saving power plan looks like.";
            }

            return null;
        }
    }

    private static readonly string[] SmiPaths =
    {
        @"C:\Windows\System32\nvidia-smi.exe",
        @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
    };

    public static NvidiaState? ReadNvidia()
    {
        var exe = SmiPaths.FirstOrDefault(File.Exists);
        if (exe is null) return null;

        var summary = RunSmi(exe,
            "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,"
            + "clocks.current.graphics,clocks.max.graphics,power.draw,power.limit,fan.speed"
            + " --format=csv,noheader,nounits");
        if (summary is null) return null;

        var first = summary.Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
        if (first is null) return null;

        var parts = first.Split(',').Select(p => p.Trim()).ToArray();
        if (parts.Length < 4) return null;

        var apps = RunSmi(exe, "--query-compute-apps=process_name --format=csv,noheader") ?? "";
        var temps = ReadTemperatures(exe);

        return new NvidiaState
        {
            Name = parts[0],
            UtilisationPercent = Int(parts[1], -1),
            MemoryUsedMb = Int(parts[2], 0),
            MemoryTotalMb = Int(parts[3], 0),
            TemperatureC = parts.Length > 4 ? Int(parts[4], -1) : -1,
            ClockMhz = parts.Length > 5 ? Int(parts[5], -1) : -1,
            ClockMaxMhz = parts.Length > 6 ? Int(parts[6], -1) : -1,
            PowerWatts = parts.Length > 7 ? Real(parts[7], -1) : -1,
            PowerLimitWatts = parts.Length > 8 ? Real(parts[8], -1) : -1,
            FanPercent = parts.Length > 9 ? Int(parts[9], -1) : -1,
            Throttling = ReadThrottleReasons(exe),
            TargetTempC = temps.GetValueOrDefault("GPU Target Temperature", -1),
            SlowdownTempC = temps.GetValueOrDefault("GPU Slowdown Temp", -1),
            ShutdownTempC = temps.GetValueOrDefault("GPU Shutdown Temp", -1),
            MaxOperatingTempC = temps.GetValueOrDefault("GPU Max Operating Temp", -1),
            MemoryTempC = temps.GetValueOrDefault("Memory Current Temp", -1),
            Programs = apps.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('['))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        static int Int(string s, int fallback)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        static double Real(string s, double fallback)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>
    /// The card's temperature thresholds, from nvidia-smi's own detail dump.
    ///
    /// Parsed out of "Name : 84 C" lines rather than asked for field by field, because the
    /// threshold fields are not all available as queries on every driver version and a missing
    /// one would take the whole query down with it.
    /// </summary>
    private static Dictionary<string, int> ReadTemperatures(string exe)
    {
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var output = RunSmi(exe, "-q -d TEMPERATURE");
        if (output is null) return found;

        foreach (var raw in output.Split('\n'))
        {
            int colon = raw.IndexOf(':');
            if (colon <= 0) continue;

            var name = raw[..colon].Trim();
            var value = raw[(colon + 1)..].Trim();
            if (name.Length == 0 || !value.EndsWith(" C", StringComparison.Ordinal)) continue;

            if (int.TryParse(value[..^2].Trim(), out var degrees)) found[name] = degrees;
        }

        return found;
    }

    /// <summary>
    /// Asks the driver what, if anything, is holding the card back.
    ///
    /// Named individually rather than read off the combined bitmask, so what comes back is a
    /// reason a person can act on - "it is too hot", "it has hit its power limit" - rather than a
    /// hexadecimal number nobody can do anything with.
    /// </summary>
    private static List<string> ReadThrottleReasons(string exe)
    {
        var reasons = new (string Field, string Meaning)[]
        {
            ("clocks_throttle_reasons.hw_thermal_slowdown", "it is too hot (the card is protecting itself)"),
            ("clocks_throttle_reasons.sw_thermal_slowdown", "it is running warm and backing off"),
            ("clocks_throttle_reasons.hw_power_brake_slowdown", "the power supply cannot keep up"),
            ("clocks_throttle_reasons.sw_power_cap", "it has hit its power limit"),
        };

        var output = RunSmi(exe,
            "--query-gpu=" + string.Join(",", reasons.Select(r => r.Field)) + " --format=csv,noheader");
        if (output is null) return new List<string>();

        var line = output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
        if (line is null) return new List<string>();

        var values = line.Split(',').Select(v => v.Trim()).ToArray();

        return reasons
            .Where((_, i) => i < values.Length
                             && values[i].Equals("Active", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Meaning)
            .ToList();
    }

    // ------------------------------------------------------------------ power

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>
    /// Whether this machine is on battery. Null when it has no battery, or cannot say.
    ///
    /// Worth asking before anything else about a laptop that is slow. Windows and the graphics
    /// driver both cut speed hard on battery, and it is the one explanation that costs nothing to
    /// rule out and makes every other measurement meaningless while it is true.
    /// </summary>
    public static bool? OnBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return null;

            // 128 means "no system battery" - a desktop, where the question does not apply.
            if ((status.BatteryFlag & 128) != 0) return null;

            return status.AcLineStatus switch
            {
                0 => true,      // running off the battery
                1 => false,     // plugged in
                _ => null,      // 255: unknown
            };
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Runs nvidia-smi with a short leash. It must never hold up a waiting request.</summary>
    private static string? RunSmi(string exe, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(6000)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }

            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                  or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ what the game chose

    /// <summary>
    /// The graphics chip the GAME picked, out of its own log.
    ///
    /// Unity writes this at startup, before anything else happens, and it is the only completely
    /// unambiguous answer to "which card is it actually drawing on" - better than anything measured
    /// from outside, because it is the game reporting its own device.
    /// </summary>
    public sealed class GameRenderer
    {
        public string Gpu { get; init; } = "";
        public string Api { get; init; } = "";
        public long VramMb { get; init; }

        public string Describe(bool running)
            => (running ? "The game is drawing on: " : "The game last drew on: ") + Gpu
               + (VramMb > 0 ? $" ({VramMb:N0} MB)" : "")
               + (Api.Length > 0 ? $"  via {Api}" : "");
    }

    public static GameRenderer? ReadGameRenderer(GameLocation location)
    {
        try
        {
            var dir = Path.Combine(location.UserDataRoot, "logs");
            if (!Directory.Exists(dir)) return null;

            var newest = new DirectoryInfo(dir)
                .GetFiles("output_log*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) return null;

            string gpu = "", api = "";
            long vram = 0;

            using var reader = new StreamReader(
                new FileStream(newest.FullName, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete));

            // It is all in the first page of the file; a play session's log runs to tens of
            // megabytes and none of the rest of it says anything about the device.
            for (int i = 0; i < 200 && reader.ReadLine() is { } line; i++)
            {
                var t = line.Trim();

                if (gpu.Length == 0 && t.Contains("GPU:", StringComparison.Ordinal))
                    gpu = After(t, "GPU:");
                else if (gpu.Length == 0 && t.StartsWith("Renderer:", StringComparison.Ordinal))
                    gpu = After(t, "Renderer:");

                if (api.Length == 0 && t.Contains("Graphics API:", StringComparison.Ordinal))
                    api = After(t, "Graphics API:");

                if (vram == 0 && t.StartsWith("VRAM:", StringComparison.Ordinal))
                    vram = long.TryParse(new string(After(t, "VRAM:").TakeWhile(char.IsDigit).ToArray()),
                                         out var v) ? v : 0;
            }

            if (gpu.Length == 0) return null;

            // Unity writes it as "NVIDIA GeForce RTX 2060 (6144 MB)"; keep the name, take the size.
            var bracket = gpu.IndexOf(" (", StringComparison.Ordinal);
            if (bracket > 0)
            {
                var inside = gpu[(bracket + 2)..].TrimEnd(')');
                if (vram == 0)
                    vram = long.TryParse(new string(inside.TakeWhile(char.IsDigit).ToArray()), out var v2) ? v2 : 0;
                gpu = gpu[..bracket];
            }

            return new GameRenderer { Gpu = gpu.Trim(), Api = api.Trim(), VramMb = vram };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        static string After(string line, string key)
        {
            int at = line.IndexOf(key, StringComparison.Ordinal);
            return at < 0 ? "" : line[(at + key.Length)..].Trim();
        }
    }

    /// <summary>
    /// Which Windows power plan is active, by name.
    ///
    /// Read from the registry rather than by running powercfg, because this is answered inside a
    /// request another machine is waiting on and starting a process to read one string is a poor
    /// trade. The well-known plan identifiers have been fixed for as long as the feature has
    /// existed; anything else is reported as its identifier rather than guessed at.
    /// </summary>
    public static string ActivePowerPlan()
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["381b4222-f694-41f0-9685-ff5bb260df2e"] = "Balanced",
            ["8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"] = "High performance",
            ["a1841308-3541-4fab-bc81-f71556f20b4a"] = "Power saver",
            ["e9a42b02-d5df-448d-aa00-03f14749eb61"] = "Ultimate performance",
        };

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");

            if (key?.GetValue("ActivePowerScheme") is not string guid || guid.Length == 0) return "";

            return known.TryGetValue(guid, out var name) ? name : guid;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ what it is being asked to draw

    /// <summary>
    /// The game's graphics settings, read from its own preferences.
    ///
    /// The question a saturated graphics card raises immediately: what is it being asked to do?
    /// A card at 100% with a nearly empty scene is not a card that is too slow for the game - it
    /// is a card being asked for more than it can give, and the difference is a settings screen
    /// rather than a new laptop.
    ///
    /// Raw numbers, not invented labels. These are stored as plain integers with no names attached
    /// anywhere in the game's files, and inventing a scale would be guessing dressed as fact. The
    /// convention throughout is that bigger costs more; view distance is in chunks and is the most
    /// expensive single setting in this game by a wide margin.
    /// </summary>
    public sealed class GraphicsSettings
    {
        public Dictionary<string, int> Values { get; init; } = new();

        /// <summary>The ones that actually move the frame rate, in the order they are worth trying.</summary>
        public static readonly string[] Expensive =
        {
            "OptionsGfxViewDistance", "OptionsGfxQualityPreset", "OptionsGfxShadowQuality",
            "OptionsGfxShadowDistance", "OptionsGfxReflectQuality", "OptionsGfxSSReflections",
            "OptionsGfxReflectShadows", "OptionsGfxSSAO", "OptionsGfxOcclusion",
            "OptionsGfxObjQuality", "OptionsGfxTerrainQuality", "OptionsGfxTreeDistance",
            "OptionsGfxGrassDistance", "OptionsGfxTexQuality", "OptionsGfxTexFilter",
            "OptionsGfxSunShafts", "OptionsGfxBloom", "OptionsGfxMotionBlurEnabled", "OptionsGfxDOF",
            "OptionsGfxUpscalerMode", "OptionsGfxFSRPreset", "OptionsGfxDynamicMode",
            "OptionsGfxDynamicMinFPS", "OptionsGfxVsync", "OptionsGfxLimitFpsInGame",
            "OptionsGfxResolution", "OptionsGfxAA",
        };

        public List<string> Describe()
        {
            var lines = new List<string>();
            foreach (var key in Expensive)
            {
                if (!Values.TryGetValue(key, out var v)) continue;
                lines.Add($"  {key["OptionsGfx".Length..],-20} {v}");
            }
            return lines;
        }
    }

    /// <summary>
    /// Reads the graphics settings out of the game's preferences.
    ///
    /// Integers only. Several neighbouring preferences are floats stored as their raw bit pattern,
    /// which read back as absurd numbers like 4607182418800017408 - so anything that does not look
    /// like a setting a person could have chosen is left out rather than printed as nonsense.
    /// </summary>
    public static GraphicsSettings? ReadGraphicsSettings()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(GameLauncher.PrefsKeyPath);
            if (key is null) return null;

            var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in key.GetValueNames())
            {
                int cut = raw.LastIndexOf("_h", StringComparison.Ordinal);
                var name = cut > 0 ? raw[..cut] : raw;
                if (!GraphicsSettings.Expensive.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                var data = key.GetValue(raw);
                long number = data switch
                {
                    int i => i,
                    long l => l,
                    string t when long.TryParse(t, out var parsed) => parsed,
                    _ => long.MinValue,
                };

                // A float smuggled in as a long. No graphics setting is a million.
                if (number == long.MinValue || number > 1_000_000 || number < -1_000_000) continue;

                values[name] = (int)number;
            }

            return values.Count == 0 ? null : new GraphicsSettings { Values = values };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
