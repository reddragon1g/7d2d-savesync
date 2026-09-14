using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SaveSync.Core.Lan;

public sealed class PairRequest
{
    public required string MachineId { get; init; }
    public required string DisplayName { get; init; }
    public required string SenderName { get; init; }
    public required string Address { get; init; }
}

public sealed class ReceivedPackage
{
    public required InboxItem Item { get; init; }
    public required string FromName { get; init; }

    /// <summary>True when this machine's own engine proved it safe and applied it immediately.</summary>
    public bool Applied { get; init; }

    public Relation Relation { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// Accepts transfers from the other PC.
///
/// Two rules keep this safe. Incoming bytes land in the inbox and never touch the game's save
/// folder until this machine's own engine has verified the manifest; and the decision to apply is
/// made here, locally, only when the relation is provably safe. A remote machine can never talk
/// this one into overwriting newer progress.
/// </summary>
public sealed class LanServer : IDisposable
{
    private readonly AppConfig _config;
    private readonly Func<TransferEngine?> _engineProvider;
    private readonly ConcurrentDictionary<string, PushSession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;

    public LanServer(AppConfig config, Func<TransferEngine?> engineProvider)
    {
        _config = config;
        _engineProvider = engineProvider;
    }

    public int Port { get; private set; }
    public bool Running => _listener is not null;

    /// <summary>
    /// Which address to listen on. Tests bind to loopback, which Windows never asks about; the app
    /// binds to every address so the other PC can actually reach it.
    /// </summary>
    public IPAddress BindAddress { get; set; } = IPAddress.Any;

    /// <summary>Non-null reason why the listener could not start, for the UI to show.</summary>
    public string? StartFailure { get; private set; }

    /// <summary>Raised after a package has been received and dealt with.</summary>
    public event Action<ReceivedPackage>? PackageArrived;

    public void Start()
    {
        if (_listener is not null) return;

        Exception? last = null;
        for (int offset = 0; offset <= FirewallSetup.PortSearchRange; offset++)
        {
            int port = _config.LanPort + offset;
            try
            {
                var listener = new TcpListener(BindAddress, port);
                listener.Start();
                _listener = listener;
                Port = port;
                StartFailure = null;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
                return;
            }
            catch (SocketException ex) { last = ex; }
        }

        StartFailure = last?.Message ?? "Could not open a network port.";
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            LanProtocol.Configure(client);
            var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

            try
            {
                using var stream = client.GetStream();

                // One connection carries one conversation; a push is many connections in sequence
                // so an interrupted file never poisons the rest.
                while (!ct.IsCancellationRequested)
                {
                    var request = await LanProtocol.ReadHeaderAsync<LanRequest>(stream, ct).ConfigureAwait(false);
                    if (request is null) return;

                    var response = await HandleAsync(request, stream, address, ct).ConfigureAwait(false);
                    await LanProtocol.WriteMessageAsync(stream, response, ct: ct).ConfigureAwait(false);

                    if (request.Op is "hello" or "pair" or "list-saves" or "request-send") return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is IOException or SocketException or InvalidDataException or EndOfStreamException)
            {
                // A dropped connection is normal; the sender retries and nothing was committed.
            }
        }
    }

    private async Task<LanResponse> HandleAsync(
        LanRequest request, Stream stream, string address, CancellationToken ct)
    {
        if (request.Version != LanProtocol.Version)
            return LanResponse.Fail("The other PC is running a different version of Save Transfer. Update both.");

        switch (request.Op)
        {
            case "hello":
                return Hello(request, address);

            case "pair":
                return Pair(request, address);

            default:
                if (!IsAuthorised(request))
                    return LanResponse.Fail("This PC has not been paired with yours yet.");
                break;
        }

        return request.Op switch
        {
            "list-saves" => ListSaves(),
            "request-send" => RequestSend(request, address),
            "push-begin" => PushBegin(request, address),
            "push-file" => await PushFileAsync(request, stream, ct).ConfigureAwait(false),
            "push-commit" => PushCommit(request),
            "push-abort" => PushAbort(request),
            _ => LanResponse.Fail($"Unknown request '{request.Op}'."),
        };
    }

    private LanResponse Hello(LanRequest request, string address)
    {
        var known = _config.FindPeer(request.MachineId);
        if (known is not null) _config.RememberPeer(request.MachineId, request.DisplayName, address);

        return new LanResponse
        {
            Ok = true,
            MachineId = _config.MachineId,
            DisplayName = _config.DisplayName,
            ToolVersion = TransferEngine.ToolVersion,
            Paired = known?.IsPaired == true,
        };
    }

    private LanResponse Pair(LanRequest request, string address)
    {
        var existing = _config.FindPeer(request.MachineId);
        if (existing?.IsPaired == true)
            return new LanResponse { Ok = true, Secret = existing.Secret, Paired = true, MachineId = _config.MachineId };

        // The two PCs introduce themselves without asking anyone anything. Auto-trust is safe here
        // because a peer cannot talk this machine into losing progress: incoming data lands in the
        // inbox, and only this machine's own engine decides whether it is safe to apply.
        var secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var peer = _config.RememberPeer(request.MachineId, request.DisplayName, address);
        peer.Secret = secret;

        try { _config.Save(); } catch (IOException) { }

        return new LanResponse { Ok = true, Secret = secret, Paired = true, MachineId = _config.MachineId };
    }

    private bool IsAuthorised(LanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Secret)) return false;
        var peer = _config.FindPeer(request.MachineId);
        return peer?.Secret is not null
               && FixedTimeEquals(peer.Secret, request.Secret);
    }

    /// <summary>Compares without leaking where two secrets first differ.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    /// <summary>Describes what this PC holds, with enough history for the far side to compare.</summary>
    private LanResponse ListSaves()
    {
        var engine = _engineProvider();
        if (engine is null) return LanResponse.Fail("This PC cannot find its 7 Days to Die saves.");

        var saves = SaveDiscovery.Enumerate(engine.Location)
            .Select(slot => new PeerSave
            {
                SaveId = slot.Passport?.SaveId ?? "",
                World = slot.World,
                SaveName = slot.SaveName,
                Passport = slot.Passport,
                SizeBytes = slot.SizeBytes,
                LastPlayedAt = slot.LastWriteUtc,
                PlayedSinceLastCopy = TransferEngine.LooksChangedSinceCommit(slot),
            })
            .ToList();

        return new LanResponse
        {
            Ok = true,
            MachineId = _config.MachineId,
            DisplayName = _config.DisplayName,
            SavesJson = Json.Write(saves),
        };
    }

    /// <summary>
    /// The other PC has noticed we hold something newer and is asking for it. Packaging and
    /// sending is harmless here - it only reads - and the far side still decides for itself
    /// whether what arrives is safe to apply.
    /// </summary>
    private LanResponse RequestSend(LanRequest request, string address)
    {
        var engine = _engineProvider();
        if (engine is null) return LanResponse.Fail("This PC cannot find its 7 Days to Die saves.");

        var blockers = TransferEngine.GlobalBlockers();
        if (blockers.Count > 0) return LanResponse.Fail(blockers[0].Message);

        if (string.IsNullOrWhiteSpace(request.SaveId)) return LanResponse.Fail("No save was named.");

        var peer = new LanPeer
        {
            MachineId = request.MachineId,
            DisplayName = request.DisplayName,
            Address = address,
            Port = request.ReplyPort > 0 ? request.ReplyPort : LanProtocol.DefaultPort,
            LastSeen = DateTimeOffset.UtcNow,
        };

        var saveId = request.SaveId!;
        _ = Task.Run(() => SendOnRequestAsync(engine, peer, saveId));

        return new LanResponse { Ok = true, Message = "Sending." };
    }

    private async Task SendOnRequestAsync(TransferEngine engine, LanPeer peer, string saveId)
    {
        string? outbox = null;
        try
        {
            var slot = SaveDiscovery.Enumerate(engine.Location)
                .FirstOrDefault(s => s.Passport?.SaveId == saveId);
            if (slot is null) return;

            outbox = Path.Combine(engine.Workspace.Staging, "outgoing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outbox);

            var package = engine.Export(slot, outbox).PackageDir;
            await new LanClient(_config).SendPackageAsync(peer, package, engine.Identity).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or TransferBlockedException or InvalidOperationException
                                  or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            // The asking side simply does not get the save this time and will ask again.
        }
        finally
        {
            if (outbox is not null)
            {
                try { PathUtil.DeleteTree(outbox); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private LanResponse PushBegin(LanRequest request, string address)
    {
        var engine = _engineProvider();
        if (engine is null) return LanResponse.Fail("This PC cannot find its 7 Days to Die saves.");

        var blockers = TransferEngine.GlobalBlockers();
        if (blockers.Count > 0) return LanResponse.Fail(blockers[0].Message);

        var free = FileOps.FreeSpace(engine.Location.SavesDir);
        if (free >= 0 && free < request.TotalBytes + (512L * 1024 * 1024))
            return LanResponse.Fail($"Not enough disk space on this PC "
                                    + $"({PathUtil.HumanBytes(free)} free, needs {PathUtil.HumanBytes(request.TotalBytes)}).");

        var token = Guid.NewGuid().ToString("N");
        var dir = Inbox.NewSlot(engine.Workspace, token);

        _sessions[token] = new PushSession
        {
            Token = token,
            Dir = dir,
            FromMachineId = request.MachineId,
            FromName = string.IsNullOrWhiteSpace(request.SenderName) ? request.DisplayName : request.SenderName,
            Address = address,
            ExpectedFiles = request.FileCount,
        };

        return new LanResponse { Ok = true, Token = token };
    }

    private async Task<LanResponse> PushFileAsync(LanRequest request, Stream stream, CancellationToken ct)
    {
        if (request.Token is null || !_sessions.TryGetValue(request.Token, out var session))
        {
            // The body still has to be drained or the connection desynchronises.
            if (request.BodyBytes > 0) await DrainAsync(stream, request.BodyBytes, ct).ConfigureAwait(false);
            return LanResponse.Fail("That transfer is no longer open.");
        }

        if (request.BodyBytes < 0 || request.BodyBytes > LanProtocol.MaxBodyBytes)
            return LanResponse.Fail("Refusing an implausibly large file.");

        var section = request.Section == PackageLayout.WorldDir ? PackageLayout.WorldDir : PackageLayout.PayloadDir;
        string target;
        try
        {
            target = Inbox.ResolveInside(session.Dir, Path.Combine(section, request.Path ?? ""));
        }
        catch (InvalidDataException ex)
        {
            await DrainAsync(stream, request.BodyBytes, ct).ConfigureAwait(false);
            return LanResponse.Fail(ex.Message);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            await LanProtocol.CopyExactAsync(stream, file, request.BodyBytes, ct).ConfigureAwait(false);
        }

        // Per-file check, so a bad transfer is caught at the file that broke rather than at the end.
        if (!string.IsNullOrWhiteSpace(request.Sha256))
        {
            var actual = Manifest.HashFile(target, ct);
            if (!string.Equals(actual, request.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(target); } catch (IOException) { }
                return LanResponse.Fail($"{request.Path} arrived corrupted.");
            }
        }

        // Restore the original timestamp. Without this the save looks freshly played the moment it
        // lands, and the next comparison invents a conflict that does not exist.
        if (request.MTimeTicks > 0)
        {
            try
            {
                File.SetLastWriteTimeUtc(target, new DateTime(request.MTimeTicks, DateTimeKind.Utc));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                // Not fatal on its own; the manifest check below is what guarantees the contents.
            }
        }

        session.Received++;
        return new LanResponse { Ok = true };
    }

    private static async Task DrainAsync(Stream stream, long bytes, CancellationToken ct)
    {
        if (bytes <= 0) return;
        await LanProtocol.CopyExactAsync(stream, Stream.Null, bytes, ct).ConfigureAwait(false);
    }

    private LanResponse PushCommit(LanRequest request)
    {
        if (request.Token is null || !_sessions.TryRemove(request.Token, out var session))
            return LanResponse.Fail("That transfer is no longer open.");

        var engine = _engineProvider();
        if (engine is null) return LanResponse.Fail("This PC cannot find its 7 Days to Die saves.");

        try
        {
            if (string.IsNullOrWhiteSpace(request.PackageInfoJson) || string.IsNullOrWhiteSpace(request.ManifestJson))
                return LanResponse.Fail("The transfer arrived without its file list.");

            File.WriteAllText(PackageLayout.Info(session.Dir), request.PackageInfoJson);
            File.WriteAllText(PackageLayout.Manifest(session.Dir), request.ManifestJson);

            // From here on this is an ordinary package, judged by this machine's own engine using
            // exactly the same code path as a USB stick.
            var plan = engine.Inspect(session.Dir);

            bool applied = false;
            string message;

            if (plan.IsOneClickSafe)
            {
                var result = engine.Import(plan, ImportChoice.Apply);
                applied = result.Applied;
                message = applied
                    ? "Installed on the other PC."
                    : "The other PC did not install it.";
                if (applied) Inbox.Discard(session.Dir);
            }
            else if (plan.HasBlockers)
            {
                message = plan.Findings.First(f => f.Severity == Severity.Blocker).Message;
            }
            else
            {
                message = "Saved on the other PC, but it needs someone there to choose - "
                          + "both PCs were played since they last matched.";
            }

            var info = PackageInfo.Load(session.Dir);
            if (info is not null)
            {
                PackageArrived?.Invoke(new ReceivedPackage
                {
                    Item = new InboxItem { Dir = session.Dir, Info = info, ReceivedAt = DateTimeOffset.UtcNow },
                    FromName = session.FromName,
                    Applied = applied,
                    Relation = plan.Relation,
                    Summary = message,
                });
            }

            return new LanResponse
            {
                Ok = true,
                Applied = applied,
                Relation = plan.Relation.ToString(),
                Message = message,
            };
        }
        catch (PathTooLongException)
        {
            return LanResponse.Fail(
                "The save's folder path is too long for Windows on this PC. "
                + "A shorter world or save name fixes it.");
        }
        catch (TransferBlockedException ex)
        {
            return LanResponse.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Inbox.Discard(session.Dir);
            return LanResponse.Fail("The other PC could not finish: " + ex.Message);
        }
    }

    private LanResponse PushAbort(LanRequest request)
    {
        if (request.Token is not null && _sessions.TryRemove(request.Token, out var session))
            Inbox.Discard(session.Dir);
        return new LanResponse { Ok = true };
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    private sealed class PushSession
    {
        public required string Token { get; init; }
        public required string Dir { get; init; }
        public required string FromMachineId { get; init; }
        public required string FromName { get; init; }
        public required string Address { get; init; }
        public int ExpectedFiles { get; init; }
        public int Received;
    }
}
