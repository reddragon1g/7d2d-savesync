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

                    // One exchange, one connection, for everything that is not part of a push.
                    // The new ops were missing from this list, so those connections were left
                    // waiting for a second request that was never coming.
                    if (request.Op is "hello" or "pair" or "list-saves" or "request-send"
                        or "get-log" or "update-offer" or "update-file"
                        or "inbox-list" or "inbox-keep-both" or "restart") return;
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
            "get-log" => GetLog(),
            "inbox-list" => InboxList(),
            "inbox-keep-both" => InboxKeepBoth(request),
            "restart" => Restart(request),
            "update-offer" => UpdateOffer(request),
            "update-file" => await UpdateFileAsync(request, stream, ct).ConfigureAwait(false),
            "request-send" => RequestSend(request, address),
            "push-begin" => PushBegin(request, address),
            "push-file" => await PushFileAsync(request, stream, ct).ConfigureAwait(false),
            "push-commit" => PushCommit(request),
            "push-abort" => PushAbort(request),
            _ => LanResponse.Fail($"Unknown request '{request.Op}'."),
        };
    }

    /// <summary>
    /// Hands over this machine's account of itself.
    ///
    /// Read-only, and available to any paired peer: it is the same text the person sitting at this
    /// PC can open from the window, and being able to ask a machine you cannot walk over to is the
    /// entire point. It says what was looked at and what was decided - no secrets, no credentials.
    /// </summary>
    private LanResponse GetLog()
    {
        var path = ActivityLog.FilePath;
        if (path is null || !File.Exists(path))
            return new LanResponse
            {
                Ok = true,
                LogLabel = _config.DisplayName,
                LogText = "",
                ToolVersion = TransferEngine.ToolVersion,
            };

        try
        {
            // Shared read: this machine is very likely writing to it at the same moment.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);

            return new LanResponse
            {
                Ok = true,
                LogLabel = _config.DisplayName,
                LogText = reader.ReadToEnd(),
                ToolVersion = TransferEngine.ToolVersion,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LanResponse.Fail("Could not read this PC's log: " + e.Message);
        }
    }

    /// <summary>
    /// Asks this PC to start its freshly installed copy and step aside.
    ///
    /// A program cannot replace itself while running, so an update that arrives over the network
    /// only takes effect at the next launch - and on a machine nobody is sitting at, that could be
    /// days. This closes that gap, and only that: it refuses while the game is open or a transfer
    /// is in flight, and it refuses outright unless there is an installed copy to hand over to,
    /// because the failure worth avoiding is a machine left with nothing running at all.
    /// </summary>
    private LanResponse Restart(LanRequest request)
    {
        if (RestartRequested is null)
            return LanResponse.Fail("This PC cannot restart itself.");

        var blockers = TransferEngine.GlobalBlockers();
        if (blockers.Count > 0) return LanResponse.Fail(blockers[0].Message);

        if (!_sessions.IsEmpty)
            return LanResponse.Fail("A transfer is going on here right now.");

        ActivityLog.Write($"asked by {request.DisplayName} to restart");
        RestartRequested.Invoke();

        return new LanResponse { Ok = true, Message = "Restarting." };
    }

    /// <summary>Raised when a peer has asked this copy to hand over to the installed one.</summary>
    public event Action? RestartRequested;

    /// <summary>Everything on this PC that is waiting for somebody to decide about it.</summary>
    private LanResponse InboxList()
    {
        var engine = _engineProvider();
        if (engine is null) return LanResponse.Fail("This PC is not set up yet.");

        var waiting = new List<WaitingSave>();

        foreach (var item in Inbox.List(engine.Workspace))
        {
            ImportPlan plan;
            try { plan = engine.Inspect(item.Dir); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { continue; }

            var evidence = SaveEvidence.Read(PackageLayout.Payload(item.Dir));

            waiting.Add(new WaitingSave
            {
                Id = Path.GetFileName(item.Dir),
                SaveName = item.Info.Passport.SaveName,
                World = item.Info.Passport.World,
                FromName = item.Info.CreatedBy,
                Relation = plan.Relation.ToString(),
                Why = Lineage.Explain(plan.Relation),
                Day = evidence.Readable ? evidence.Day : 0,
                Players = evidence.PlayerIds.Count,
                Bytes = item.Info.PayloadBytes,
                ReceivedAt = item.ReceivedAt,
                SuggestedName = plan.SuggestedNewName(),
            });
        }

        return new LanResponse { Ok = true, InboxJson = Json.Write(waiting) };
    }

    /// <summary>
    /// Installs a waiting save BESIDE whatever is already here, under a different name.
    ///
    /// The only decision that can be made from another machine, and it is allowed precisely
    /// because of what it cannot do: it never replaces, renames or removes anything that is
    /// already on this PC. The worst it can produce is a spare save somebody deletes later. Every
    /// other answer to a clash - taking the incoming copy, keeping this one - destroys one of the
    /// two, and that stays a decision for a person sitting at this machine.
    /// </summary>
    private LanResponse InboxKeepBoth(LanRequest request)
    {
        var engine = _engineProvider();
        if (engine is null) return LanResponse.Fail("This PC is not set up yet.");

        if (string.IsNullOrWhiteSpace(request.InboxId))
            return LanResponse.Fail("No waiting save was named.");

        var item = Inbox.List(engine.Workspace)
            .FirstOrDefault(i => string.Equals(Path.GetFileName(i.Dir), request.InboxId,
                StringComparison.OrdinalIgnoreCase));

        if (item is null) return LanResponse.Fail("There is nothing waiting by that name on this PC.");

        try
        {
            var plan = engine.Inspect(item.Dir);
            plan.InstallAsName = string.IsNullOrWhiteSpace(request.InstallAsName)
                ? plan.SuggestedNewName()
                : request.InstallAsName;

            ActivityLog.Write($"asked by {request.DisplayName} to keep both for "
                + $"{plan.Info.Passport.SaveName} - installing it as \"{plan.InstallAsName}\"");

            var result = engine.Import(plan, ImportChoice.InstallAsNewSave);
            if (!result.Applied) return LanResponse.Fail("It was not installed.");

            Inbox.Discard(item.Dir);

            return new LanResponse
            {
                Ok = true,
                Applied = true,
                Message = $"Installed as \"{plan.InstallAsName}\". Nothing already here was touched.",
            };
        }
        catch (TransferBlockedException ex)
        {
            return LanResponse.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return LanResponse.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Somebody is offering to replace the program on this PC. Answered before a byte is sent.
    ///
    /// Refused unless this machine has been explicitly told to accept updates - pairing alone is
    /// never enough for this one - and unless what is on offer is actually newer than what is
    /// here, so a stale copy on somebody's stick cannot walk a machine backwards.
    /// </summary>
    private LanResponse UpdateOffer(LanRequest request)
    {
        if (!_config.AllowRemoteUpdate)
            return LanResponse.Fail(
                "This PC has not been set to accept program updates over the network. Somebody has "
                + "to switch that on at that PC first.");

        if (!Version.TryParse(request.OfferedVersion, out var offered))
            return LanResponse.Fail("The offered version could not be read.");

        var mine = RemoteUpdate.RunningVersion;
        if (offered <= mine)
            return LanResponse.Fail($"This PC is already on {mine}.");

        if (string.IsNullOrWhiteSpace(request.OfferedSha256))
            return LanResponse.Fail("The offered program did not come with a checksum.");

        ActivityLog.Write($"offered an update to {offered} by {request.DisplayName} - accepting");
        return new LanResponse { Ok = true, ToolVersion = mine.ToString() };
    }

    /// <summary>
    /// Receives the program itself and stages it. Verified against the checksum that was declared
    /// up front, and not put anywhere it would be run until it matches.
    /// </summary>
    private async Task<LanResponse> UpdateFileAsync(LanRequest request, Stream stream, CancellationToken ct)
    {
        if (!_config.AllowRemoteUpdate)
            return LanResponse.Fail("This PC does not accept program updates over the network.");

        var engine = _engineProvider();
        if (engine is null) return LanResponse.Fail("This PC is not set up yet.");

        try
        {
            var received = await RemoteUpdate.ReceiveDetailedAsync(
                engine.Workspace, stream, request.BodyBytes, request.OfferedSha256 ?? "", ct)
                .ConfigureAwait(false);

            if (!received.Ok)
            {
                ActivityLog.Write($"an update to {request.OfferedVersion} was not kept: {received.Reason}");
                return LanResponse.Fail("It was not kept on this PC: " + received.Reason);
            }

            var staged = received.Path!;
            ActivityLog.Write($"an update to {request.OfferedVersion} arrived and checked out; staged at {staged}");
            UpdateStaged?.Invoke(staged, request.OfferedVersion ?? "");

            return new LanResponse { Ok = true, Message = "Received. It will be put in place here." };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LanResponse.Fail("Could not save the update on this PC: " + e.Message);
        }
    }

    /// <summary>Raised when a verified update is sitting ready. The app decides when to apply it.</summary>
    public event Action<string, string>? UpdateStaged;

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

        bool named = !string.IsNullOrWhiteSpace(request.SaveId)
                     || (!string.IsNullOrWhiteSpace(request.World) && !string.IsNullOrWhiteSpace(request.SaveName));
        if (!named) return LanResponse.Fail("No save was named.");

        var peer = new LanPeer
        {
            MachineId = request.MachineId,
            DisplayName = request.DisplayName,
            Address = address,
            Port = request.ReplyPort > 0 ? request.ReplyPort : LanProtocol.DefaultPort,
            LastSeen = DateTimeOffset.UtcNow,
        };

        var saveId = request.SaveId ?? "";
        var world = request.World ?? "";
        var saveName = request.SaveName ?? "";
        _ = Task.Run(() => SendOnRequestAsync(engine, peer, saveId, world, saveName));

        return new LanResponse { Ok = true, Message = "Sending." };
    }

    private async Task SendOnRequestAsync(
        TransferEngine engine, LanPeer peer, string saveId, string world, string saveName)
    {
        string? outbox = null;
        try
        {
            var all = SaveDiscovery.Enumerate(engine.Location);

            // By id when there is one - that survives a rename. By name when there is not, which is
            // the case for a save that has never been copied off this machine.
            var slot = !string.IsNullOrWhiteSpace(saveId)
                ? all.FirstOrDefault(s => string.Equals(s.Passport?.SaveId, saveId, StringComparison.OrdinalIgnoreCase))
                : null;

            slot ??= all.FirstOrDefault(s =>
                string.Equals(s.World, world, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.SaveName, saveName, StringComparison.OrdinalIgnoreCase));

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
