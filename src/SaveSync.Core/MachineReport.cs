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

    /// <summary>How hard the memory system is working. Above ~90% something is being squeezed.</summary>
    public int MemoryLoadPercent { get; init; }

    /// <summary>Commit charge against the limit. Near the limit means Windows is paging to keep up.</summary>
    public long CommittedBytes { get; init; }
    public long CommitLimitBytes { get; init; }

    /// <summary>The biggest memory users on that PC, so "what is eating it" has an answer.</summary>
    public List<ProcessUse> TopProcesses { get; init; } = new();

    /// <summary>The most recent performance line the game wrote, if it has written one.</summary>
    public GameStats? Game { get; init; }

    /// <summary>
    /// How this PC is set up to start the game: EasyAntiCheat, renderer, any extra parameters.
    ///
    /// Worth seeing from another machine because it is both a thing people deliberately change and
    /// a thing that explains behaviour - EAC is off on these two PCs by choice, and anything that
    /// started the game would have to keep it that way.
    /// </summary>
    public string LaunchSetup { get; init; } = "";

    /// <summary>
    /// The command line that PC's own launcher last started the game with, verbatim.
    ///
    /// The settings file says what was chosen; this says what was actually done. They can disagree
    /// - a setting changed since the last launch has not taken effect yet - and when the question
    /// is "is EasyAntiCheat in play on that machine", the answer is here rather than there.
    /// </summary>
    public string LastLaunch { get; init; } = "";

    /// <summary>Which save the game is in, read out of its own log. Empty when it has loaded none.</summary>
    public string LoadedSave { get; init; } = "";

    /// <summary>
    /// Whether this PC is currently set to skip the spawn screen. Null when it has never been set.
    ///
    /// Reported because this program borrows that setting to start a game unattended and is meant
    /// to hand it back. Something that quietly changes a setting on somebody else's machine should
    /// at minimum be willing to say so.
    /// </summary>
    public bool? SpawnButtonSkipped { get; init; }

    /// <summary>Whole-machine processor use, measured over a window. -1 when it could not be read.</summary>
    public double CpuPercent { get; init; } = -1;

    /// <summary>The busiest processes by processor, which is a different list from the hungriest.</summary>
    public List<SystemLoad.ProcessLoad> Busiest { get; init; } = new();

    /// <summary>What the game itself is using, whether or not it is among the busiest.</summary>
    public SystemLoad.ProcessLoad? GameLoad { get; init; }

    /// <summary>The NVIDIA card, and crucially whether the game is on it.</summary>
    public SystemLoad.NvidiaState? Nvidia { get; init; }

    /// <summary>The chip the game reported drawing on, from its own log.</summary>
    public SystemLoad.GameRenderer? Renderer { get; init; }

    /// <summary>
    /// How much of the commit limit is page file rather than real memory.
    ///
    /// Windows sizes the page file automatically, so this is not a setting anybody chose. What
    /// makes it worth reporting is the comparison: a machine whose commit charge sits below its
    /// physical memory is not paging, and paging can be crossed off without arguing about it.
    /// </summary>
    public long PageFileBytes { get; init; }

    /// <summary>What the game is being asked to draw. The other half of "the card is at 100%".</summary>
    public SystemLoad.GraphicsSettings? Graphics { get; init; }

    /// <summary>True when this is a laptop running on battery. Null when it has no battery.</summary>
    public bool? OnBattery { get; init; }

    /// <summary>Which Windows power plan is active. A power-saving plan caps clocks on its own.</summary>
    public string PowerPlan { get; init; } = "";

    /// <summary>Which chip Windows has been told to give the game, when it has been told anything.</summary>
    public string GpuPreference { get; init; } = "";

    public static MachineReport Read(GameLocation? location)
    {
        // One measured window, taken before anything else, so every load figure below describes
        // the same two seconds rather than three different moments stitched together.
        var load = SystemLoad.Measure(TimeSpan.FromSeconds(2));

        return new MachineReport
        {
            MachineName = Machine.Name,
            Os = RuntimeInformation.OSDescription,
            Cpu = RegistryString(
                      @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "unknown",
            Cores = Environment.ProcessorCount,
            Gpu = string.Join(" + ", AllGpus()),
            TotalMemoryBytes = Memory().Total,
            FreeMemoryBytes = Memory().Free,
            SavesDriveFreeBytes = location is null ? -1 : FileOps.FreeSpace(location.SavesDir),
            GameRunning = GamePaths.IsGameRunning(),
            MemoryLoadPercent = Memory().LoadPercent,
            CommittedBytes = Memory().Committed,
            CommitLimitBytes = Memory().CommitLimit,
            TopProcesses = ProcessUse.Biggest(10),
            Game = location is null ? null : GameStats.ReadLatest(location),
            LaunchSetup = location is null ? "" : GameLauncher.ReadSettings(location).Describe(),
            LastLaunch = location is null ? "" : DescribeLastLaunch(location),
            LoadedSave = location is null ? "" : GameLauncher.ReadLoadedSave(location)?.Describe() ?? "",
            SpawnButtonSkipped = GameLauncher.SpawnButtonSkipped(),
            CpuPercent = load.CpuPercent,
            Busiest = load.Busiest,
            GameLoad = load.Game,
            Nvidia = SystemLoad.ReadNvidia(),
            Renderer = location is null ? null : SystemLoad.ReadGameRenderer(location),
            PageFileBytes = Math.Max(0, Memory().CommitLimit - Memory().Total),
            Graphics = SystemLoad.ReadGraphicsSettings(),
            OnBattery = SystemLoad.OnBattery(),
            PowerPlan = SystemLoad.ActivePowerPlan(),
            GpuPreference = DescribeGpuPreference(location),
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

        if (CommitLimitBytes > 0)
        {
            var pressure = MemoryLoadPercent >= 90 ? "  <- under real pressure"
                : MemoryLoadPercent >= 80 ? "  <- getting tight" : "";
            lines.Add($"Memory in use: {MemoryLoadPercent}%{pressure}");
            lines.Add($"Committed: {PathUtil.HumanBytes(CommittedBytes)} of "
                + $"{PathUtil.HumanBytes(CommitLimitBytes)} - anything near the limit means Windows "
                + "is paging to disk, which on an old machine is most of what 'running badly' is.");
        }

        lines.Add(GameRunning ? "The game is running right now." : "The game is not running.");
        if (LoadedSave.Length > 0) lines.Add("Save: " + LoadedSave);
        if (LaunchSetup.Length > 0) lines.Add("Launcher settings: " + LaunchSetup);
        if (LastLaunch.Length > 0)
        {
            lines.Add("Last launch: " + LastLaunch);
            if (EacDisagreement() is { } note) lines.Add("  " + note);
        }
        if (SpawnButtonSkipped == true)
            lines.Add("Spawn screen: skipped (this program borrowed that setting; it is given back "
                      + "when the game closes)");
        if (Game is not null) lines.Add(Game.Describe());

        // ---- graphics. On a laptop with two chips this is the first thing to settle, because
        // every other explanation is a waste of time until it is ruled out.
        lines.Add("");
        if (Renderer is not null) lines.Add(Renderer.Describe(GameRunning));
        if (Nvidia is not null)
        {
            lines.Add("NVIDIA card: " + Nvidia.Describe()
                      + (GameRunning ? (Nvidia.GameIsOnIt ? "  -  the game IS on it"
                                                          : "  -  the game is NOT on it") : ""));
        }
        if (WrongChip() is { } chip) lines.Add("  >> " + chip);
        if (GpuPreference.Length > 0) lines.Add("Windows is told to give the game: " + GpuPreference);
        if (Nvidia?.Heat() is { } heat) lines.Add(heat);
        if (Nvidia?.HeldBack() is { } held) lines.Add("  >> " + held);
        lines.Add("Power: " + OnBattery switch
        {
            true => "ON BATTERY. Windows and the graphics driver both cut speed hard on battery; "
                    + "plug it in before judging anything else.",
            false => "plugged in.",
            _ => "no battery, or could not be read.",
        } + (PowerPlan.Length > 0 ? $"  Windows power plan: {PowerPlan}." : ""));

        // ---- processor
        if (CpuPercent >= 0)
        {
            var busy = CpuPercent >= 90 ? "  <- saturated" : CpuPercent >= 70 ? "  <- working hard" : "";
            lines.Add($"Processor: {CpuPercent:0}% across all {Cores} threads{busy}");
        }
        if (GameLoad is not null)
            lines.Add($"  the game itself: {GameLoad.CpuPercent:0.0}% of the whole machine "
                      + $"(about {GameLoad.CpuPercent * Cores / 100.0:0.0} threads' worth)");

        // ---- paging
        lines.Add(PagingVerdict());

        if (Graphics is not null)
        {
            lines.Add("");
            lines.Add("Graphics settings (bigger costs more; view distance is in chunks "
                      + "and is by far the most expensive):");
            lines.AddRange(Graphics.Describe());
        }

        if (Busiest.Count > 0)
        {
            lines.Add("");
            lines.Add("Busiest right now (processor):");
            foreach (var proc in Busiest) lines.Add("  " + proc.Describe());
        }

        if (TopProcesses.Count > 0)
        {
            lines.Add("");
            lines.Add("Biggest memory users:");
            foreach (var proc in TopProcesses)
                lines.Add("  " + proc.Describe());
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Says so, in as many words, when the game is drawing on the wrong graphics chip.
    ///
    /// This is worth stating rather than leaving to be worked out, because the two situations look
    /// identical from a distance and call for opposite responses. A laptop reporting "Intel UHD
    /// Graphics" is usually a laptop with a perfectly good card sitting idle next to it, and no
    /// amount of turning settings down will fix being on the wrong one.
    /// </summary>
    private string? WrongChip()
    {
        if (Renderer is null) return null;

        var drawingOnIntegrated =
            Renderer.Gpu.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            || Renderer.Gpu.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase)
            || Renderer.Gpu.Contains("Vega", StringComparison.OrdinalIgnoreCase);

        var hasDiscrete = Gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                          || Gpu.Contains("RTX", StringComparison.OrdinalIgnoreCase)
                          || Gpu.Contains("GTX", StringComparison.OrdinalIgnoreCase);

        if (drawingOnIntegrated && hasDiscrete)
            return "THE GAME IS ON THE WRONG CHIP. This PC has a proper graphics card, and the "
                   + "game is drawing on the built-in one. Windows Settings > System > Display > "
                   + "Graphics > 7DaysToDie.exe > Options > High performance, then restart the game.";

        if (Nvidia is { GameIsOnIt: false } && GameRunning && !drawingOnIntegrated)
            return "The game is running but is not on the NVIDIA card, which is worth a look.";

        return null;
    }

    /// <summary>
    /// Whether this machine is paging, said plainly enough to close the question.
    ///
    /// Worth stating either way. "Maybe it is the page file" is the kind of theory that survives
    /// indefinitely unless somebody produces a number, and the number is usually no.
    /// </summary>
    private string PagingVerdict()
    {
        if (CommitLimitBytes <= 0 || TotalMemoryBytes <= 0) return "Paging: could not be read.";

        var pageFile = PageFileBytes > 0 ? PathUtil.HumanBytes(PageFileBytes) : "none";

        if (CommittedBytes < TotalMemoryBytes)
            return $"Paging: not an issue. {PathUtil.HumanBytes(CommittedBytes)} committed against "
                   + $"{PathUtil.HumanBytes(TotalMemoryBytes)} of real memory, so it all fits without "
                   + $"touching the page file (which is {pageFile}).";

        return $"Paging: POSSIBLE. {PathUtil.HumanBytes(CommittedBytes)} committed exceeds "
               + $"{PathUtil.HumanBytes(TotalMemoryBytes)} of real memory, so some of it is going to "
               + $"the page file ({pageFile}) and that is slow.";
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

    /// <summary>
    /// Says so when the launcher settings and the actual last launch disagree about EasyAntiCheat.
    ///
    /// They can, and on both of these machines they do: the settings file says EAC is on while
    /// every real launch carries -noeac. A settings file is what somebody chose; the launch is
    /// what happened, and a setting changed after the last launch has not taken effect yet.
    /// Reporting only the first would have answered "is EAC in play over there" with the wrong
    /// answer, confidently.
    /// </summary>
    private string? EacDisagreement()
    {
        var actuallyOff = LastLaunch.Contains("-noeac", StringComparison.OrdinalIgnoreCase);
        var actuallyOn = LastLaunch.Contains("_EAC.exe", StringComparison.OrdinalIgnoreCase);
        var settingsSayOn = LaunchSetup.Contains("EAC ON", StringComparison.Ordinal);

        if (actuallyOff && settingsSayOn)
            return "EAC is OFF in practice - the settings file says on, but the last launch did not use it.";
        if (actuallyOn && !settingsSayOn)
            return "EAC is ON in practice, whatever the settings file says.";

        return null;
    }

    /// <summary>Which chip Windows has been told to hand the game, if it has been told at all.</summary>
    private static string DescribeGpuPreference(GameLocation? location)
    {
        var install = GamePaths.ResolveInstall(location?.InstallDir);
        if (install is null) return "";

        var exe = Path.Combine(install, GamePaths.ProcessName + ".exe");
        var (choice, raw) = GpuChoice.Current(exe);

        return raw is null ? "not set - left to Windows" : GpuChoice.Describe(choice);
    }

    /// <summary>The last launch as one readable line, or nothing when the game has never run here.</summary>
    private static string DescribeLastLaunch(GameLocation location)
    {
        var last = GameLauncher.LastLauncherInvocation(location);
        if (last is null) return "";

        return Path.GetFileName(last.Value.Exe) + " " + string.Join(" ", last.Value.Args);
    }

    /// <summary>
    /// EVERY display adapter, not just the first.
    ///
    /// Reporting one was actively misleading: a laptop showing "Intel UHD Graphics" is usually a
    /// laptop with a perfectly good discrete card sitting idle beside it, and the difference
    /// between those two situations is the difference between "this machine cannot run the game"
    /// and "the game is pointed at the wrong chip".
    /// </summary>
    private static List<string> AllGpus()
    {
        var found = new List<string>();
        try
        {
            const string root = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) return found;

            foreach (var name in key.GetSubKeyNames())
            {
                if (!int.TryParse(name, out _)) continue;

                using var adapter = key.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is string desc
                    && !string.IsNullOrWhiteSpace(desc)
                    && !found.Contains(desc, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(desc);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }

        if (found.Count == 0) found.Add("unknown");
        return found;
    }

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

    private static (long Total, long Free, int LoadPercent, long Committed, long CommitLimit) Memory()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                long limit = (long)status.TotalPageFile;
                long committed = limit - (long)status.AvailPageFile;
                return ((long)status.TotalPhys, (long)status.AvailPhys, (int)status.MemoryLoad, committed, limit);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }

        return (-1, -1, 0, 0, 0);
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

    /// <summary>
    /// Chunk game objects: how many separate things the renderer is being handed.
    ///
    /// The number that speaks to "is it the base". A plain landscape sits well under a hundred;
    /// every placed block that is its own object - doors, hatches, electrical, spikes, storage,
    /// anything with a model rather than a terrain face - adds to this, and a big base pushes it
    /// up hard. Chunks says how much world is loaded; this says how much of it has to be drawn.
    /// </summary>
    public int ChunkObjects { get; init; }

    /// <summary>Dropped items lying in the world. A pile of loot bags is a real cost.</summary>
    public int Items { get; init; }

    /// <summary>
    /// What the frame rate has actually been doing, not just its last value.
    ///
    /// The last line is the worst one to judge by: the game writes a sample every thirty seconds
    /// including while a world is loading and while it is shutting down, so the final reading of a
    /// session is routinely single digits and says nothing at all about playing. A run of samples,
    /// with the worst and best of them, is the difference between a measurement and a guess.
    /// </summary>
    public int SampleCount { get; init; }
    public double FpsLow { get; init; }
    public double FpsHigh { get; init; }
    public double FpsTypical { get; init; }
    public DateTimeOffset SampledFrom { get; init; }

    /// <summary>
    /// The frame rate over time, oldest first, as "HH:mm:ss=fps".
    ///
    /// A range says how bad it gets; only the shape says WHY. A machine that starts fast and slides
    /// steadily downwards while nothing in the world changes is a machine getting hot - and that
    /// looks nothing like a scene that is simply too heavy, which is slow from the first frame and
    /// stays there. Those two have completely different answers, and the average cannot tell them
    /// apart.
    /// </summary>
    public List<string> FpsOverTime { get; init; } = new();

    /// <summary>Average of the first quarter of the samples against the last quarter.</summary>
    public double FpsEarly { get; init; }
    public double FpsLate { get; init; }

    public string Describe()
    {
        var last = $"Game: last sample {Fps:0.0} fps at {At.ToLocalTime():HH:mm:ss}  -  "
                   + $"{Players} player(s), {Zombies} zombies, {Chunks} chunks, "
                   + $"{ChunkObjects} objects to draw, {Items} loose items, memory {RssMb:0} MB";

        if (SampleCount <= 1) return last + "  (one sample only - not worth much)";

        var text = last + Environment.NewLine
               + $"      over {SampleCount} samples ({SampledFrom.ToLocalTime():HH:mm:ss}-{At.ToLocalTime():HH:mm:ss}): "
               + $"typical {FpsTypical:0.0} fps, worst {FpsLow:0.0}, best {FpsHigh:0.0}";

        if (FpsOverTime.Count > 0)
            text += Environment.NewLine + "      over time: " + string.Join("  ", FpsOverTime);

        if (Shape() is { } shape) text += Environment.NewLine + "      >> " + shape;

        return text;
    }

    /// <summary>
    /// What the shape of the frame rate says, as opposed to its average.
    ///
    /// Deliberately conservative about calling it: a session that has only just started is still
    /// loading, and reading a loading dip as a decline would be exactly the kind of confident
    /// wrong answer this is meant to prevent.
    /// </summary>
    public string? Shape()
    {
        if (SampleCount < 8 || FpsEarly <= 0 || FpsLate <= 0) return null;

        var drop = (FpsEarly - FpsLate) / FpsEarly;

        if (drop >= 0.4)
            return $"It STARTED at about {FpsEarly:0.0} fps and is now about {FpsLate:0.0} - it has lost "
                   + $"{drop * 100:0}% while the world stood still. Something is getting worse over "
                   + "time, which is what heat looks like and is not what a heavy scene looks like.";

        if (drop <= -0.4)
            return $"It began at about {FpsEarly:0.0} fps and has climbed to {FpsLate:0.0} - the early "
                   + "figures were the world still loading, so judge it by the later ones.";

        return $"It has held steady, about {FpsEarly:0.0} fps at the start and {FpsLate:0.0} now. "
               + "Whatever is limiting it was limiting it from the first frame.";
    }

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

            var samples = new List<GameStats>();
            foreach (var line in tail.Split('\n'))
            {
                var parsed = Parse(line, newest.LastWriteTimeUtc);
                if (parsed is not null) samples.Add(parsed);
            }

            if (samples.Count == 0) return null;

            var recent = samples.TakeLast(40).ToList();
            var rates = recent.Select(r => r.Fps).OrderBy(f => f).ToList();
            var last = recent[^1];

            return new GameStats
            {
                Fps = last.Fps, HeapMb = last.HeapMb, RssMb = last.RssMb,
                Chunks = last.Chunks, Players = last.Players, Zombies = last.Zombies,
                Entities = last.Entities, PlayedFor = last.PlayedFor, At = last.At,
                ChunkObjects = last.ChunkObjects, Items = last.Items,
                SampleCount = recent.Count,
                SampledFrom = recent[0].At,
                FpsOverTime = recent.Select(r => $"{r.At.ToLocalTime():HH:mm:ss}={r.Fps:0.0}").ToList(),
                FpsEarly = Slice(recent, fromStart: true),
                FpsLate = Slice(recent, fromStart: false),
                FpsLow = rates[0],
                FpsHigh = rates[^1],
                FpsTypical = rates[rates.Count / 2],   // the middle one, so a loading dip cannot skew it
            };
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

    /// <summary>Average frame rate over the first or last quarter of a run of samples.</summary>
    private static double Slice(List<GameStats> samples, bool fromStart)
    {
        if (samples.Count == 0) return 0;

        int take = Math.Max(1, samples.Count / 4);
        var part = fromStart ? samples.Take(take) : samples.TakeLast(take);
        return part.Average(s => s.Fps);
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
            ChunkObjects = (int)Number("CGO"),
            Items = (int)Number("Items"),
            Players = (int)Number("Ply"),
            Zombies = (int)Number("Zom"),
            Entities = (int)Number("Ent"),
            PlayedFor = played.Success ? played.Groups[1].Value : "",
            At = at,
        };
    }
}


/// <summary>One process and what it is costing, for answering "what is eating this machine".</summary>
public sealed class ProcessUse
{
    public required string Name { get; init; }
    public int Id { get; init; }
    public long WorkingSetBytes { get; init; }

    /// <summary>True for things Windows itself needs. Never offered as something to close.</summary>
    public bool Essential { get; init; }

    public string Describe()
        => $"{Name,-28} {PathUtil.HumanBytes(WorkingSetBytes),-10}"
           + (Essential ? " (part of Windows)" : "");

    /// <summary>
    /// Things that must never be suggested as closable, whatever they are using.
    ///
    /// A list of what NOT to touch is the only safe way round this: anything not recognised is
    /// still somebody's program, so the answer to "can we free memory" is a suggestion for a
    /// person to weigh, never an action taken on their behalf.
    /// </summary>
    public static bool IsEssential(string processName) => Windows.Contains(processName);

    private static readonly HashSet<string> Windows = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "svchost", "fontdrvhost", "dwm", "explorer", "ctfmon", "sihost",
        "taskhostw", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "dllhost", "conhost", "audiodg", "spoolsv", "WmiPrvSE", "MsMpEng",
        "SecurityHealthService", "NisSrv", "LsaIso", "WUDFHost", "SystemSettings",
    };

    public static List<ProcessUse> Biggest(int count)
    {
        try
        {
            return System.Diagnostics.Process.GetProcesses()
                .Select(p =>
                {
                    try { return new ProcessUse
                    {
                        Name = p.ProcessName,
                        Id = p.Id,
                        WorkingSetBytes = p.WorkingSet64,
                        Essential = Windows.Contains(p.ProcessName),
                    }; }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        return null;
                    }
                    finally { p.Dispose(); }
                })
                .Where(p => p is not null)
                .Select(p => p!)
                .OrderByDescending(p => p.WorkingSetBytes)
                .Take(count)
                .ToList();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new List<ProcessUse>();
        }
    }
}
