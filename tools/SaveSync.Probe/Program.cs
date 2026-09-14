using SaveSync.Core;

// Developer probe. Drives the engine headlessly so the GUI can be exercised from a known state,
// and prints what the discovery layer sees on this machine.
//
//   probe discover
//   probe list     <userdata>
//   probe adopt    <userdata> [world] [saveName]
//   probe export   <userdata> <destination>
//   probe play     <userdata> <world> <saveName>     - simulate a play session
//   probe inspect  <userdata> <packageDir>
//   probe import   <userdata> <packageDir> [apply|take|keep]

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Developer escape hatch, and deliberately ONLY in this tool - the shipped program has no way to
// set it. The guard it bypasses exists for real saves; these are throwaway folders, and the
// alternative is being unable to test anything while the game happens to be open.
if (Environment.GetEnvironmentVariable("SAVESYNC_PROBE_IGNORE_GAME") == "1")
{
    TransferEngine.IsGameRunningProbe = () => false;
    Console.WriteLine("(probe: game-running guard bypassed for this run)");
}

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "discover";

try
{
    switch (cmd)
    {
        case "discover": Discover(); break;
        case "list": List(At(1)); break;
        case "adopt": Adopt(At(1), Arg(2) ?? "Navezgane", Arg(3) ?? "My Game"); break;
        case "export": Export(At(1), At(2)); break;
        case "play": Play(At(1), At(2), At(3)); break;
        case "inspect": Inspect(At(1), At(2)); break;
        case "kinship": Kinship(At(1), At(2)); break;
        case "peers": Peers(int.TryParse(Arg(1), out var secs) ? secs : 8); break;
        case "peerlog": PeerLog(int.TryParse(Arg(1), out var wait) ? wait : 8, Arg(2)); break;
        case "pushupdate": PushUpdate(At(1), int.TryParse(Arg(2), out var w2) ? w2 : 8, Arg(3)); break;
        case "import": Import(At(1), At(2), Arg(3) ?? "apply"); break;
        default:
            Console.WriteLine($"unknown command: {cmd}");
            return 2;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

return 0;

string? Arg(int i) => args.Length > i ? args[i] : null;
string At(int i) => Arg(i) ?? throw new ArgumentException($"missing argument {i}");

GameLocation Locate(string userData)
    => GamePaths.Discover(userData, Environment.GetEnvironmentVariable("SAVESYNC_PROBE_INSTALL"))
       ?? throw new InvalidOperationException($"not a user data folder: {userData}");

TransferEngine Engine(string userData)
{
    var loc = Locate(userData);
    var engine = new TransferEngine(new AppConfig { SnapshotsToKeep = 10 }, loc);
    ActivityLog.Open(engine.Workspace, Machine.Name + " (probe)");
    return engine;
}

/// <summary>Listens for other PCs on this network and says what answered.</summary>
void Peers(int seconds)
{
    var config = AppConfig.Load();
    Console.WriteLine($"this PC   : {config.DisplayName}  ({config.MachineId})");
    Console.WriteLine($"networking: {(config.UseNetwork ? "on" : "OFF")}   "
        + $"firewall rule: {(SaveSync.Core.Lan.FirewallSetup.IsConfigured() ? "present" : "MISSING")}");
    Console.WriteLine($"listening for {seconds}s...");
    Console.WriteLine();

    using var discovery = new SaveSync.Core.Lan.Discovery(config) { PersonName = "probe" };
    discovery.Start();
    if (discovery.StartFailure is not null) Console.WriteLine($"discovery failed to start: {discovery.StartFailure}");

    Thread.Sleep(TimeSpan.FromSeconds(seconds));

    var found = discovery.Peers.ToList();
    if (found.Count == 0)
    {
        Console.WriteLine("nothing answered.");
        return;
    }

    foreach (var p in found)
        Console.WriteLine($"  {p.Label,-18} {p.DisplayName,-18} {p.Address}:{p.Port}  last seen {p.LastSeen:HH:mm:ss}");
}

/// <summary>Fetches another PC's log and prints it.</summary>
void PeerLog(int seconds, string? which)
{
    var config = AppConfig.Load();

    using var discovery = new SaveSync.Core.Lan.Discovery(config) { PersonName = "probe" };
    discovery.Start();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));

    var peers = discovery.Peers.ToList();
    if (peers.Count == 0) { Console.WriteLine("no other PC answered."); return; }

    var wanted = which is null
        ? peers
        : peers.Where(p => p.DisplayName.Contains(which, StringComparison.OrdinalIgnoreCase)
                           || p.Label.Contains(which, StringComparison.OrdinalIgnoreCase)).ToList();

    foreach (var peer in wanted)
    {
        Console.WriteLine($"================ {peer.Label} ({peer.DisplayName}) at {peer.Address} ================");
        var report = new SaveSync.Core.Lan.LanClient(config)
            .GetLogAsync(peer, "probe").GetAwaiter().GetResult();

        if (report is null) { Console.WriteLine("  no answer."); continue; }

        Console.WriteLine($"  version: {report.ToolVersion}");
        Console.WriteLine(report.Text.Length == 0 ? "  (its log is empty)" : report.Text);
        Console.WriteLine();
    }
}

/// <summary>Hands a newer program to every PC on this network that will take one.</summary>
void PushUpdate(string exePath, int seconds, string? which)
{
    if (!File.Exists(exePath)) { Console.WriteLine($"no such file: {exePath}"); return; }

    var config = AppConfig.Load();
    Console.WriteLine($"offering {exePath}");
    Console.WriteLine($"  {new FileInfo(exePath).Length:N0} bytes  sha {SaveSync.Core.Lan.RemoteUpdate.Sha256Of(exePath)[..16]}...");
    Console.WriteLine($"  this build is {SaveSync.Core.Lan.RemoteUpdate.RunningVersion}");
    Console.WriteLine();

    using var discovery = new SaveSync.Core.Lan.Discovery(config) { PersonName = "probe" };
    discovery.Start();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));

    var peers = discovery.Peers
        .Where(p => which is null || p.DisplayName.Contains(which, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (peers.Count == 0) { Console.WriteLine("no other PC answered."); return; }

    var client = new SaveSync.Core.Lan.LanClient(config);
    foreach (var peer in peers)
    {
        Console.Write($"  {peer.DisplayName,-18} {peer.Address,-16} ");
        var result = client.PushUpdateAsync(peer, exePath, "probe").GetAwaiter().GetResult();
        Console.WriteLine(result.Sent ? "SENT - " + result.Message : "refused - " + result.Message);
    }
}

void Kinship(string saveA, string saveB)
{
    var a = SaveEvidence.Read(saveA);
    var b = SaveEvidence.Read(saveB);

    Console.WriteLine($"A: {saveA}");
    Console.WriteLine($"   {a.GameVersion}  world={a.WorldFingerprint}  {a.Describe()}");
    Console.WriteLine($"B: {saveB}");
    Console.WriteLine($"   {b.GameVersion}  world={b.WorldFingerprint}  {b.Describe()}");
    Console.WriteLine();

    var v = SaveKinship.Compare(a, b);
    Console.WriteLine($"VERDICT: {v.Kind}");
    Console.WriteLine($"  {v.Headline}");
    foreach (var r in v.Reasons) Console.WriteLine($"    - {r}");
}

void Discover()
{
    Console.WriteLine($"machine   : {Machine.Identity}");
    Console.WriteLine($"steam     : {SteamLocator.FindSteamRoot() ?? "(not found)"}");
    foreach (var lib in SteamLocator.LibraryFolders()) Console.WriteLine($"  library : {lib}");
    Console.WriteLine($"install   : {SteamLocator.FindAppInstallDir(SteamLocator.SevenDaysAppId) ?? "(not found)"}");
    Console.WriteLine();

    foreach (var c in GamePaths.UserDataCandidates())
        Console.WriteLine($"  [{(c.Valid ? "OK " : "no ")}] {c.Path}  <- {c.Provenance}");

    var loc = GamePaths.Discover();
    Console.WriteLine();
    Console.WriteLine(loc is null ? "RESULT: not found" : $"RESULT: {loc.UserDataRoot}  ({loc.Provenance})");
    if (loc is not null)
    {
        var ver = SaveDiscovery.ReadGameVersionHint(loc);
        Console.WriteLine($"game ver  : {(string.IsNullOrWhiteSpace(ver) ? "(not detected)" : ver)}");
        Console.WriteLine($"mods      : {Mods.Travelling(loc).Count} would travel, "
            + $"{Mods.Enumerate(loc).Count(m => m.ShippedWithGame)} came with the game");
    }
}

void List(string userData)
{
    var loc = Locate(userData);
    foreach (var s in SaveDiscovery.Enumerate(loc))
    {
        Console.WriteLine($"{s.SaveName} ({s.World})  {PathUtil.HumanBytes(s.SizeBytes)}  {s.FileCount} files  {s.WorldKind}");
        Console.WriteLine(s.Passport is null
            ? "    not set up"
            : $"    v{s.Passport.Ordinal} {s.Passport.VersionId} by {s.Passport.LastPlayedOn} chain={s.Passport.Chain.Count}");
    }
}

void Adopt(string userData, string world, string saveName)
{
    var loc = Locate(userData);
    var slot = SaveDiscovery.Find(loc, world, saveName)
               ?? throw new InvalidOperationException($"no save {world}/{saveName}");
    var p = SaveDiscovery.Adopt(slot, loc, SaveDiscovery.ReadGameVersionHint(loc));
    Console.WriteLine($"adopted {p.Describe()} saveId={p.SaveId}");
}

void Export(string userData, string destination)
{
    var loc = Locate(userData);
    var engine = Engine(userData);
    // Export adopts an unknown save on the spot, so there is nothing to filter out here.
    foreach (var slot in SaveDiscovery.Enumerate(loc))
    {
        var r = engine.Export(slot, destination);
        Console.WriteLine($"exported v{r.Passport.Ordinal} {PathUtil.HumanBytes(r.Bytes)} -> {r.PackageDir}");
        foreach (var f in r.Findings) Console.WriteLine($"    {f.Severity}: {f.Message}");
    }
}

void Play(string userData, string world, string saveName)
{
    var loc = Locate(userData);
    var slot = SaveDiscovery.Find(loc, world, saveName)
               ?? throw new InvalidOperationException($"no save {world}/{saveName}");

    // Mutate a couple of region files and move timestamps forward, the way a session would.
    var rnd = new Random();
    var region = Path.Combine(slot.Folder, "Region");
    Directory.CreateDirectory(region);
    foreach (var name in new[] { "r.0.0.7rg", $"r.{rnd.Next(1, 40)}.{rnd.Next(1, 40)}.7rg" })
    {
        var buf = new byte[8192];
        rnd.NextBytes(buf);
        File.WriteAllBytes(Path.Combine(region, name), buf);
    }

    var main = Path.Combine(slot.Folder, "main.ttw");
    if (File.Exists(main))
    {
        var bytes = File.ReadAllBytes(main);
        if (bytes.Length > 16) { rnd.NextBytes(bytes.AsSpan(0, 16)); File.WriteAllBytes(main, bytes); }
    }

    var now = DateTime.UtcNow;
    foreach (var f in Manifest.EnumerateFiles(slot.Folder))
    {
        try { new FileInfo(f).LastWriteTimeUtc = now; } catch (IOException) { }
    }

    Console.WriteLine($"simulated a play session on {world}/{saveName}");
}

void Import(string userData, string packageDir, string choice)
{
    var engine = Engine(userData);
    var plan = engine.Inspect(packageDir);
    var pick = choice switch
    {
        "take" => ImportChoice.TakeIncomingKeepBackup,
        "keep" => ImportChoice.KeepLocal,
        _ => ImportChoice.Apply,
    };
    var result = engine.Import(plan, pick);
    Console.WriteLine($"relation={plan.Relation} applied={result.Applied} backup={result.Backup?.Id ?? "(none)"}");
    foreach (var f in result.Findings) Console.WriteLine($"    {f.Severity}: {f.Message}");
}

void Inspect(string userData, string packageDir)
{
    var engine = Engine(userData);
    var plan = engine.Inspect(packageDir);
    Console.WriteLine($"relation      : {plan.Relation}");
    Console.WriteLine($"one click safe: {plan.IsOneClickSafe}");
    Console.WriteLine($"needs choice  : {plan.NeedsHumanChoice}");
    Console.WriteLine($"blockers      : {plan.HasBlockers}");
    Console.WriteLine($"target        : {plan.TargetFolder}");
    foreach (var f in plan.Findings) Console.WriteLine($"    {f.Severity}: {f.Message}");
}
