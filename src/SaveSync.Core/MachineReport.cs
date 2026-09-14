using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SaveSync.Core;

/// <summary>
/// What a PC is, and how the game is actually behaving on it.
///
/// "It runs badly on the laptop" is not something anybody can act on from another machine, and
/// walking over to look is the thing this whole program exists to avoid. The game already writes
/// everything needed - frame rate, memory, how much world is loaded - into its own log every
/// thirty seconds, so none of this requires touching the game or being anywhere near it.
/// </summary>
public sealed class MachineReport
{
    public string MachineName { get; init; } = "";
    public string Os { get; init; } = "";
    public string Cpu { get; init; } = "";
    public int Cores { get; init; }
    public string Gpu { get; init; } = "";
    public long TotalMemoryBytes { get; init; }
    public long FreeMemoryBytes { get; init; }
    public long SavesDriveFreeBytes { get; init; }

    public bool GameRunning { get; init; }

    /// <summary>The most recent performance line the game wrote, if it has written one.</summary>
    public GameStats? Game { get; init; }

    public static MachineReport Read(GameLocation? location)
    {
        return new MachineReport
        {
            MachineName = Machine.Name,
            Os = RuntimeInformation.OSDescription,
            Cpu = RegistryString(
                      @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "unknown",
            Cores = Environment.ProcessorCount,
            Gpu = FirstGpu() ?? "unknown",
            TotalMemoryBytes = Memory().Total,
            FreeMemoryBytes = Memory().Free,
            SavesDriveFreeBytes = location is null ? -1 : FileOps.FreeSpace(location.SavesDir),
            GameRunning = GamePaths.IsGameRunning(),
            Game = location is null ? null : GameStats.ReadLatest(location),
        };
    }

    public string Describe()
    {
        var lines = new List<string>
        {
            $"{MachineName}  -  {Os}",
            $"CPU: {Cpu} ({Cores} threads)",
            $"GPU: {Gpu}",
            $"RAM: {PathUtil.HumanBytes(TotalMemoryBytes)} total, {PathUtil.HumanBytes(FreeMemoryBytes)} free",
        };

        if (SavesDriveFreeBytes >= 0)
            lines.Add($"Free space where the saves live: {PathUtil.HumanBytes(SavesDriveFreeBytes)}");

        lines.Add(GameRunning ? "The game is running right now." : "The game is not running.");
        if (Game is not null) lines.Add(Game.Describe());

        return string.Join(Environment.NewLine, lines);
    }

    private static string? RegistryString(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The first display adapter Windows lists. Enough to say what class of machine it is.</summary>
    private static string? FirstGpu()
    {
        try
        {
            const string root = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) return null;

            foreach (var name in key.GetSubKeyNames())
            {
                if (!int.TryParse(name, out _)) continue;

                using var adapter = key.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc))
                    return desc;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static (long Total, long Free) Memory()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return ((long)status.TotalPhys, (long)status.AvailPhys);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }

        return (-1, -1);
    }
}

/// <summary>One of the game's own periodic performance lines.</summary>
public sealed class GameStats
{
    public double Fps { get; init; }
    public double HeapMb { get; init; }
    public double RssMb { get; init; }
    public int Chunks { get; init; }
    public int Players { get; init; }
    public int Zombies { get; init; }
    public int Entities { get; init; }
    public string PlayedFor { get; init; } = "";
    public DateTimeOffset At { get; init; }

    public string Describe()
        => $"Game: {Fps:0.0} fps  -  {Players} player(s), {Zombies} zombies, {Entities} entities  -  "
           + $"{Chunks} chunks loaded  -  memory {RssMb:0} MB  -  measured {At.ToLocalTime():HH:mm:ss}";

    /// <summary>
    /// The most recent line of the form the game writes every thirty seconds:
    ///
    ///   Time: 1.49m FPS: 112.32 Heap: 2004.3MB ... Chunks: 325 ... Ply: 2 Zom: 0 Ent: 3 (5) ... RSS: 4787.4MB
    ///
    /// Only the tail of the file is read. A play session's log reaches tens of megabytes, and the
    /// only interesting line is the last one.
    /// </summary>
    public static GameStats? ReadLatest(GameLocation location)
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

            var tail = Tail(newest.FullName, 128 * 1024);
            if (tail.Length == 0) return null;

            GameStats? latest = null;
            foreach (var line in tail.Split('\n'))
            {
                var parsed = Parse(line, newest.LastWriteTimeUtc);
                if (parsed is not null) latest = parsed;
            }
            return latest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Tail(string path, int bytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        long start = Math.Max(0, fs.Length - bytes);
        fs.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[(int)Math.Min(bytes, fs.Length - start)];
        int read = fs.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static GameStats? Parse(string line, DateTime fileTime)
    {
        if (!line.Contains("FPS:", StringComparison.Ordinal)) return null;

        double Number(string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, key + @":\s*([0-9.]+)");
            return m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        double fps = Number("FPS");
        if (fps <= 0) return null;

        var when = System.Text.RegularExpressions.Regex.Match(line, @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})");
        var at = when.Success && DateTimeOffset.TryParse(when.Groups[1].Value, out var parsed)
            ? parsed
            : new DateTimeOffset(fileTime, TimeSpan.Zero);

        var played = System.Text.RegularExpressions.Regex.Match(line, @"Time:\s*([0-9.]+[a-z])");

        return new GameStats
        {
            Fps = fps,
            HeapMb = Number("Heap"),
            RssMb = Number("RSS"),
            Chunks = (int)Number("Chunks"),
            Players = (int)Number("Ply"),
            Zombies = (int)Number("Zom"),
            Entities = (int)Number("Ent"),
            PlayedFor = played.Success ? played.Groups[1].Value : "",
            At = at,
        };
    }
}
