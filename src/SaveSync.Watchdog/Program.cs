using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SaveSync.Core;
using SaveSync.Core.Lan;

// A small program whose only job is to make sure the big one is alive, and to be reachable when it
// is not.
//
// Everything else here has been built on the assumption that the main program is running and able
// to answer. Every time that assumption broke - a listener that failed to bind, an installer stuck
// on its own locked file, a window sitting on a question nobody was there to answer - the machine
// became unreachable and somebody had to walk over to it. A fix that lives inside the thing that
// is broken cannot be delivered.
//
// So this is deliberately tiny and deliberately dull. It does not touch saves, it has no window,
// and it holds no state worth losing. It watches, it restarts, it accepts a new copy of the main
// program, and it can start or stop the game. If it ever needs replacing itself, that is the one
// job left for a USB stick.

const int WatchPort = 47366;
const string SingleInstance = "SaveSync.7DaysToDie.Watchdog";

using var single = new Mutex(false, SingleInstance);
if (!single.WaitOne(TimeSpan.FromSeconds(2)))
{
    Log("another watchdog is already running; standing down");
    return 0;
}

Log($"watchdog {Version()} starting on port {WatchPort}");

using var stopping = new CancellationTokenSource();
AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

var listener = StartListener();
var watching = Task.Run(() => WatchAsync(stopping.Token));

try { await Task.WhenAll(AcceptAsync(listener, stopping.Token), watching); }
catch (OperationCanceledException) { }

return 0;

static string Version() =>
    typeof(AppConfig).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

static string AppExe() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "SaveSync", "app", "SaveSync.exe");

/// <summary>Its own log, kept beside the program it watches rather than inside the game's folders.</summary>
static void Log(string what)
{
    try
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaveSync");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "watchdog.log");

        // Trimmed rather than rotated: nothing here is worth keeping for long.
        try
        {
            if (new FileInfo(path).Length > 256 * 1024) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        File.AppendAllText(path,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {what}{Environment.NewLine}");
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
}

static TcpListener StartListener()
{
    var l = new TcpListener(IPAddress.Any, WatchPort);
    l.Start();
    return l;
}

/// <summary>True when the main program is answering on its own port.</summary>
static bool AppIsAnswering(AppConfig config)
{
    try
    {
        using var probe = new TcpClient();
        return probe.ConnectAsync(IPAddress.Loopback, config.LanPort)
                    .Wait(TimeSpan.FromSeconds(3)) && probe.Connected;
    }
    catch (Exception e) when (e is SocketException or IOException or AggregateException)
    {
        return false;
    }
}

/// <summary>
/// Starts the main program if it is not answering.
///
/// Always with --background, because nobody is there: a copy that comes up showing a window - or
/// worse, a question - is the failure this exists to end.
/// </summary>
static bool StartApp(string why)
{
    var exe = AppExe();
    if (!File.Exists(exe)) { Log($"cannot start the app ({why}): it is not installed"); return false; }

    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--background",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
        Log($"started the app ({why})");
        return true;
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        Log($"could not start the app ({why}): {e.Message}");
        return false;
    }
}

static void StopApp()
{
    foreach (var p in SafeProcesses("SaveSync"))
    {
        try
        {
            p.CloseMainWindow();
            if (!p.WaitForExit(8000)) p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            Log($"stopped the app (pid {p.Id})");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception
                                  or NotSupportedException) { }
        finally { p.Dispose(); }
    }
}

static Process[] SafeProcesses(string name)
{
    try { return Process.GetProcessesByName(name); }
    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        return Array.Empty<Process>();
    }
}

/// <summary>
/// The loop that makes this worth having: if the main program stops answering, start it again.
///
/// Given a generous grace period because a handover deliberately leaves a gap, and restarting
/// something in the middle of replacing itself would be worse than waiting.
/// </summary>
static async Task WatchAsync(CancellationToken ct)
{
    var missedSince = (DateTimeOffset?)null;

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var config = AppConfig.Load();

            // A setting borrowed to start the game, whose owner never got the chance to hand it
            // back. This process outlives the one that borrowed it, which makes it the right
            // place to notice - it is a no-op unless a note is actually waiting.
            GameLauncher.RestorePendingSpawnPref();

            if (AppIsAnswering(config))
            {
                if (missedSince is not null) Log("the app is answering again");
                missedSince = null;
            }
            else
            {
                missedSince ??= DateTimeOffset.UtcNow;

                if (DateTimeOffset.UtcNow - missedSince.Value > TimeSpan.FromSeconds(90))
                {
                    Log("the app has not answered for 90s");
                    StopApp();                       // clear out anything wedged or half-started
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    StartApp("it was not answering");
                    missedSince = null;
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { Log("watch loop: " + e.Message); }

        try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
    }
}

static async Task AcceptAsync(TcpListener listener, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { continue; }

        _ = Task.Run(() => ServeAsync(client, ct), ct);
    }
}

static async Task ServeAsync(TcpClient client, CancellationToken ct)
{
    using (client)
    {
        try
        {
            LanProtocol.Configure(client);
            await using var stream = client.GetStream();

            var request = await LanProtocol.ReadHeaderAsync<LanRequest>(stream, ct).ConfigureAwait(false);
            if (request is null) return;

            var response = await HandleAsync(request, stream, ct).ConfigureAwait(false);
            await LanProtocol.WriteMessageAsync(stream, response, ct: ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException
                                  or EndOfStreamException or OperationCanceledException) { }
    }
}

/// <summary>
/// Only a machine this one has already paired with may say anything.
///
/// The watchdog can replace a program and stop processes, so it asks for the same shared secret
/// the main program uses and nothing without one gets past here.
/// </summary>
static bool Authorised(AppConfig config, LanRequest request)
{
    if (string.IsNullOrWhiteSpace(request.Secret)) return false;
    var peer = config.FindPeer(request.MachineId);
    return peer?.Secret is not null
           && string.Equals(peer.Secret, request.Secret, StringComparison.Ordinal);
}

static async Task<LanResponse> HandleAsync(LanRequest request, Stream stream, CancellationToken ct)
{
    var config = AppConfig.Load();

    if (!Authorised(config, request))
        return LanResponse.Fail("This PC's watchdog has not been paired with yours.");

    switch (request.Op)
    {
        case "watch-ping":
            return new LanResponse
            {
                Ok = true,
                MachineId = config.MachineId,
                DisplayName = config.DisplayName,
                ToolVersion = Version(),
                Message = AppIsAnswering(config) ? "the app is answering" : "the app is NOT answering",
            };

        case "watch-restart-app":
            Log($"asked by {request.DisplayName} to restart the app");
            StopApp();
            await Task.Delay(2000, ct).ConfigureAwait(false);
            return StartApp("asked to")
                ? new LanResponse { Ok = true, Message = "Restarted." }
                : LanResponse.Fail("It would not start.");

        case "watch-install-app":
        {
            // The whole reason this exists: replacing the main program from outside it, so a copy
            // that cannot install its own replacement is no longer a machine somebody has to visit.
            if (!config.AllowRemoteUpdate)
                return LanResponse.Fail("This PC does not accept program updates over the network.");

            var workspace = WorkspaceFor(config);
            if (workspace is null) return LanResponse.Fail("This PC is not set up yet.");

            var received = await RemoteUpdate.ReceiveDetailedAsync(
                workspace, stream, request.BodyBytes, request.OfferedSha256 ?? "", ct).ConfigureAwait(false);

            if (!received.Ok)
            {
                Log("update not kept: " + received.Reason);
                return LanResponse.Fail("It was not kept: " + received.Reason);
            }

            // Stopped FIRST, so the file being replaced is not one somebody is still running - the
            // exact knot that made this necessary in the first place.
            StopApp();
            await Task.Delay(1500, ct).ConfigureAwait(false);

            try
            {
                var target = AppExe();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                foreach (var old in Directory.GetFiles(Path.GetDirectoryName(target)!, "SaveSync.exe*.old"))
                {
                    try { File.Delete(old); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }

                if (File.Exists(target))
                    File.Move(target, $"{target}.{DateTime.UtcNow:yyyyMMddHHmmss}.old");

                File.Copy(received.Path!, target, overwrite: true);
                try { File.Delete(received.Path!); } catch (IOException) { }

                Log($"installed {request.OfferedVersion} and starting it");
                StartApp("just updated");

                return new LanResponse { Ok = true, Message = $"Installed {request.OfferedVersion} and started it." };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log("could not put the update in place: " + e.Message);
                StartApp("update failed; putting the old one back");
                return LanResponse.Fail("Could not put it in place: " + e.Message);
            }
        }

        case "watch-game-stop":
        {
            var running = SafeProcesses(GamePaths.ProcessName);
            if (running.Length == 0) return new LanResponse { Ok = true, Message = "The game was not running." };

            // Asked to close, never killed. The game writes the world as it goes, and killing it
            // mid-write produces exactly the broken save everything else here exists to prevent.
            foreach (var p in running)
            {
                try { p.CloseMainWindow(); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }

            Log($"asked by {request.DisplayName} to close the game");
            return new LanResponse { Ok = true, Message = "Asked the game to close. It saves on the way out." };
        }

        case "watch-game-start":
        {
            // Same launcher the main program uses, so a machine whose main program is wedged can
            // still be put into the right save rather than merely switched on.
            var result = GameLauncher.Start(config.Locate(), request.World, request.SaveName, request.DisplayName);

            Log(result.Ok
                ? $"asked by {request.DisplayName} to start the game: {result.CommandLine}"
                : $"asked by {request.DisplayName} to start the game, refused: {result.Message}");

            return result.Ok
                ? new LanResponse { Ok = true, Message = result.Message }
                : LanResponse.Fail(result.Message);
        }

        default:
            return LanResponse.Fail($"The watchdog does not know '{request.Op}'.");
    }
}

static Workspace? WorkspaceFor(AppConfig config)
{
    var location = config.Locate();
    return location is null ? null : new Workspace(location.UserDataRoot);
}
