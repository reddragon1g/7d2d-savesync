using System.Diagnostics;
using System.Globalization;

namespace SaveSync.Core;

/// <summary>
/// Watching a graphics card several times a second, to catch something that repeats.
///
/// Every other measurement here is a single reading or a thirty-second average, and both are blind
/// to the thing being chased: a stutter with a RHYTHM. "It hitches about every second and a half"
/// is not a performance problem in general, it is a specific piece of periodic work, and an
/// average taken over thirty seconds cannot see it at all - thirty smooth seconds with twenty
/// hitches in them average out to exactly thirty smooth seconds.
///
/// So this samples five times a second and then asks the series whether it repeats, by correlating
/// it against itself at every offset. A card whose clock rises and collapses on a fixed cycle
/// shows up as a clear peak at that cycle's length, and the number that comes back can be compared
/// against what a person says they can feel.
/// </summary>
public static class GpuPulse
{
    private static readonly string[] SmiPaths =
    {
        @"C:\Windows\System32\nvidia-smi.exe",
        @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
    };

    public sealed class Reading
    {
        public double Seconds { get; init; }
        public int ClockMhz { get; init; }
        public int UtilisationPercent { get; init; }
        public int TemperatureC { get; init; }
        public double PowerWatts { get; init; }
    }

    public sealed class Result
    {
        public List<Reading> Readings { get; init; } = new();

        /// <summary>The cycle length found in the clock speed, in seconds. Zero when there is none.</summary>
        public double PeriodSeconds { get; init; }

        /// <summary>How strongly it repeats, 0 to 1. Below about 0.3 is not worth believing.</summary>
        public double Strength { get; init; }

        public string Describe()
        {
            if (Readings.Count == 0) return "The card could not be sampled.";

            var clocks = Readings.Select(r => r.ClockMhz).ToList();
            var utils = Readings.Select(r => r.UtilisationPercent).ToList();
            var temps = Readings.Select(r => r.TemperatureC).ToList();

            var lines = new List<string>
            {
                $"{Readings.Count} readings over {Readings[^1].Seconds:0.0}s, five a second:",
                $"  clock  {clocks.Min(),5} - {clocks.Max(),5} MHz",
                $"  load   {utils.Min(),5} - {utils.Max(),5} %",
                $"  temp   {temps.Min(),5} - {temps.Max(),5} C",
                "",
            };

            // The temperature series, because how fast a card SHEDS heat once the load stops is
            // the one measurement that tells you whether anything is moving air over it - and it
            // does not depend on the driver being willing to report a fan speed at all.
            if (temps.Count > 4 && temps[0] != temps[^1])
            {
                var span = Readings[^1].Seconds - Readings[0].Seconds;
                var change = temps[^1] - temps[0];
                lines.Add($"  temperature went {temps[0]}C -> {temps[^1]}C over {span:0.0}s "
                          + $"({change * 60.0 / Math.Max(1, span):+0.0;-0.0} C per minute)");
                lines.Add("");
            }

            if (PeriodSeconds > 0 && Strength >= 0.3)
            {
                lines.Add($">> The card's speed RISES AND FALLS on a cycle of about "
                          + $"{PeriodSeconds:0.0} seconds (confidence {Strength:0.00}).");
                lines.Add("   A clock that climbs, overheats, collapses and climbs again produces a "
                          + "hitch every time round, and the period of the hitch is the period of "
                          + "that cycle.");
            }
            else if (clocks.Max() - clocks.Min() < 50)
            {
                lines.Add(">> The card's speed is STEADY. Whatever the rhythm is, it is not the "
                          + "graphics card oscillating - look at the game itself.");
            }
            else
            {
                lines.Add($">> The card's speed varies ({clocks.Min()}-{clocks.Max()} MHz) but not "
                          + "on a regular cycle, so it is responding to the work rather than "
                          + "driving it.");
            }

            lines.Add("");
            lines.Add("clock MHz, one per reading:");
            lines.Add("  " + string.Join(" ", clocks));
            lines.Add("");
            lines.Add("temperature C, one per reading:");
            lines.Add("  " + string.Join(" ", temps));

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Samples the card for a few seconds and looks for a repeating pattern.
    ///
    /// One nvidia-smi held open and read line by line, rather than one launched per sample: a
    /// process launch costs about as long as the interval being measured, which would make the
    /// instrument the loudest thing in the measurement.
    /// </summary>
    public static Result? Sample(int seconds = 20)
    {
        var exe = SmiPaths.FirstOrDefault(File.Exists);
        if (exe is null) return null;

        seconds = Math.Clamp(seconds, 5, 60);
        var readings = new List<Reading>();

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--query-gpu=clocks.current.graphics,utilization.gpu,temperature.gpu,power.draw"
                            + " --format=csv,noheader,nounits -lms 200",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            var started = Stopwatch.StartNew();

            try
            {
                while (started.Elapsed.TotalSeconds < seconds)
                {
                    var line = p.StandardOutput.ReadLine();
                    if (line is null) break;

                    var parts = line.Split(',').Select(x => x.Trim()).ToArray();
                    if (parts.Length < 3) continue;

                    readings.Add(new Reading
                    {
                        Seconds = started.Elapsed.TotalSeconds,
                        ClockMhz = Int(parts[0]),
                        UtilisationPercent = Int(parts[1]),
                        TemperatureC = Int(parts[2]),
                        PowerWatts = parts.Length > 3 ? Real(parts[3]) : 0,
                    });
                }
            }
            finally
            {
                try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                  or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (readings.Count < 10) return new Result { Readings = readings };

        var (period, strength) = Repetition(readings.Select(r => (double)r.ClockMhz).ToList(), 0.2);

        return new Result { Readings = readings, PeriodSeconds = period, Strength = strength };

        static int Int(string s)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;

        static double Real(string s)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;
    }

    // ------------------------------------------------------------------ the game itself

    /// <summary>
    /// The game process, ten times a second: processor, memory, threads.
    ///
    /// Written after the graphics card was measured perfectly steady - 300 MHz, 100%, 83C, flat
    /// across twenty-five seconds - while the person playing reported a hitch every second and a
    /// half. A steady card and a rhythmic stutter means the rhythm belongs to the game, and none
    /// of the instruments here could see inside it.
    ///
    /// Memory is sampled for a specific reason. A managed runtime collecting garbage produces a
    /// sawtooth: memory climbs as the game allocates, drops when the collector runs, and the game
    /// stops dead while it does. At a steady allocation rate that repeats on a fixed cycle - which
    /// is exactly what "it hitches every second and a half" sounds like, and it would be invisible
    /// to every other measurement taken so far.
    /// </summary>
    public sealed class GameReading
    {
        public double Seconds { get; init; }

        /// <summary>Processor used in this sample, as a share of one core.</summary>
        public double CpuPercent { get; init; }

        public long PrivateBytes { get; init; }
        public int Threads { get; init; }
    }

    public sealed class GameResult
    {
        public List<GameReading> Readings { get; init; } = new();

        public double CpuPeriodSeconds { get; init; }
        public double CpuStrength { get; init; }

        public double MemoryPeriodSeconds { get; init; }
        public double MemoryStrength { get; init; }

        /// <summary>How many times memory fell sharply - each one is a collection.</summary>
        public int Collections { get; init; }

        public string Describe()
        {
            if (Readings.Count < 10) return "The game was not running, or could not be sampled.";

            var cpu = Readings.Select(r => r.CpuPercent).ToList();
            var mem = Readings.Select(r => r.PrivateBytes / 1024.0 / 1024.0).ToList();

            var lines = new List<string>
            {
                $"{Readings.Count} readings over {Readings[^1].Seconds:0.0}s, ten a second:",
                $"  processor  {cpu.Min(),6:0.0} - {cpu.Max(),6:0.0} % of one core  (typical {Median(cpu):0.0})",
                $"  memory     {mem.Min(),6:0} - {mem.Max(),6:0} MB",
                $"  threads    {Readings.Min(r => r.Threads),6} - {Readings.Max(r => r.Threads)}",
                "",
            };

            bool found = false;

            // A cycle in the memory series means nothing on its own, and saying otherwise was a
            // bug worth naming: this reported "that is garbage collection" twice about a series
            // that wobbled by 40 MB out of 7,300 - half a per cent - with zero drops in it, on the
            // sole basis that correlating noise against itself found a period. Noise correlates.
            //
            // Collection is a SAWTOOTH: memory has to actually climb and actually fall back. Both
            // the amplitude and the drops are now required before the word is used.
            var swing = mem.Max() - mem.Min();
            var swingShare = mem.Max() > 0 ? swing / mem.Max() : 0;

            if (MemoryPeriodSeconds > 0 && MemoryStrength >= 0.3 && Collections >= 3 && swingShare >= 0.02)
            {
                lines.Add($">> MEMORY rises and falls on a cycle of about {MemoryPeriodSeconds:0.0} "
                          + $"seconds (confidence {MemoryStrength:0.00}), swinging {swing:0} MB with "
                          + $"{Collections} sharp drops.");
                lines.Add("   That is garbage collection. The game stops while it runs, so the "
                          + "period of the collection is the period of the stutter.");
                found = true;
            }
            else if (swingShare < 0.02)
            {
                lines.Add($">> Memory is FLAT - it moves {swing:0} MB out of {mem.Max():0}, which is "
                          + "noise. Whatever the rhythm is, it is not garbage collection.");
            }

            if (CpuPeriodSeconds > 0 && CpuStrength >= 0.3)
            {
                lines.Add($">> PROCESSOR use repeats on a cycle of about {CpuPeriodSeconds:0.0} "
                          + $"seconds (confidence {CpuStrength:0.00}) - something in the game is "
                          + "doing a burst of work on a timer.");
                found = true;
            }

            if (!found)
            {
                lines.Add(">> Nothing in the game repeats on a regular cycle at this resolution. "
                          + "Whatever the rhythm is, it is not the processor and not collection - "
                          + "which leaves the graphics card, the disk, or the network.");
            }

            lines.Add("");
            lines.Add("memory MB, one per reading:");
            lines.Add("  " + string.Join(" ", mem.Select(m => $"{m:0}")));

            return string.Join(Environment.NewLine, lines);

            static double Median(List<double> v)
            {
                var s = v.OrderBy(x => x).ToList();
                return s[s.Count / 2];
            }
        }
    }

    /// <summary>
    /// Samples the running game and asks whether anything about it repeats.
    ///
    /// Ten times a second, which is fast enough to resolve a cycle of a second and a half and slow
    /// enough that the measuring does not become part of what is measured.
    /// </summary>
    public static GameResult? SampleGame(int seconds = 20)
    {
        seconds = Math.Clamp(seconds, 5, 60);

        var game = FindGame();
        if (game is null) return null;

        var readings = new List<GameReading>();
        var interval = TimeSpan.FromMilliseconds(100);

        try
        {
            var started = Stopwatch.StartNew();
            var lastCpu = game.TotalProcessorTime;
            var lastAt = started.Elapsed;

            while (started.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(interval);

                try
                {
                    game.Refresh();
                    if (game.HasExited) break;

                    var now = started.Elapsed;
                    var cpu = game.TotalProcessorTime;
                    var window = (now - lastAt).TotalSeconds;

                    if (window > 0)
                    {
                        readings.Add(new GameReading
                        {
                            Seconds = now.TotalSeconds,
                            CpuPercent = (cpu - lastCpu).TotalSeconds / window * 100.0,
                            PrivateBytes = game.PrivateMemorySize64,
                            Threads = game.Threads.Count,
                        });
                    }

                    lastCpu = cpu;
                    lastAt = now;
                }
                catch (Exception e) when (e is InvalidOperationException
                                          or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    break;
                }
            }
        }
        finally
        {
            game.Dispose();
        }

        if (readings.Count < 10) return new GameResult { Readings = readings };

        var cpuSeries = readings.Select(r => r.CpuPercent).ToList();
        var memSeries = readings.Select(r => (double)r.PrivateBytes).ToList();

        var (cpuPeriod, cpuStrength) = Repetition(cpuSeries, 0.1);
        var (memPeriod, memStrength) = Repetition(memSeries, 0.1);

        return new GameResult
        {
            Readings = readings,
            CpuPeriodSeconds = cpuPeriod,
            CpuStrength = cpuStrength,
            MemoryPeriodSeconds = memPeriod,
            MemoryStrength = memStrength,
            Collections = SharpDrops(memSeries),
        };
    }

    private static Process? FindGame()
    {
        try
        {
            var all = Process.GetProcessesByName(GamePaths.ProcessName);
            if (all.Length == 0) return null;

            for (int i = 1; i < all.Length; i++) all[i].Dispose();
            return all[0];
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// How many times the series fell sharply. Each fall in a memory series is a collection.
    ///
    /// A threshold rather than any drop, because memory wobbles constantly; what marks a
    /// collection is giving back a noticeable share of what was being held.
    /// </summary>
    private static int SharpDrops(List<double> series)
    {
        if (series.Count < 3) return 0;

        double typical = series.Average();
        if (typical <= 0) return 0;

        int drops = 0;
        for (int i = 1; i < series.Count; i++)
            if (series[i - 1] - series[i] > typical * 0.01) drops++;

        return drops;
    }

    /// <summary>
    /// The strongest repeating cycle in a series, by correlating it against itself at each offset.
    ///
    /// Offsets below half a second are skipped: at this sample rate they pick up sampling noise
    /// rather than anything real, and a confident answer of "it repeats every 0.2 seconds" would
    /// be worse than no answer.
    /// </summary>
    public static (double Period, double Strength) Repetition(List<double> series, double intervalSeconds)
    {
        if (series.Count < 10) return (0, 0);

        double mean = series.Average();
        var centred = series.Select(v => v - mean).ToList();

        double variance = centred.Sum(v => v * v);
        if (variance <= 0.0001) return (0, 0);         // perfectly flat: nothing repeats

        int minLag = Math.Max(2, (int)(0.5 / intervalSeconds));
        int maxLag = series.Count / 2;

        double bestScore = 0;
        int bestLag = 0;

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i + lag < centred.Count; i++) sum += centred[i] * centred[i + lag];

            // Normalised by the overlap so short and long offsets compare fairly.
            double score = sum / variance * series.Count / (series.Count - lag);

            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }

        return (bestLag * intervalSeconds, Math.Clamp(bestScore, 0, 1));
    }
}
