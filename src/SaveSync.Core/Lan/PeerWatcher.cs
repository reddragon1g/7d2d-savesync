namespace SaveSync.Core.Lan;

/// <summary>One save, as it stands between this PC and the other one.</summary>
public sealed class PeerNews
{
    public required LanPeer Peer { get; init; }
    public required PeerSave Remote { get; init; }
    public SaveSlot? Local { get; init; }
    public Relation Relation { get; init; }
    public SyncDirection Direction { get; init; }
    public required string Summary { get; init; }

    public string Display => Remote.Display;
    public bool WorthFetching => Direction == SyncDirection.ToPc;
}

/// <summary>
/// Keeps an eye on the other PC and works out, on its own, whether it is holding something newer.
///
/// This is what makes the trip home seamless: the laptop was played away from the house, and the
/// moment both machines are on the same network again the desktop notices without anyone asking
/// it to. Noticing is all it does - nothing is moved until someone says so, or until the
/// unattended path proves it is safe.
/// </summary>
public sealed class PeerWatcher : IDisposable
{
    private readonly AppConfig _config;
    private readonly Func<IReadOnlyCollection<LanPeer>> _peerProvider;
    private readonly LanClient _client;
    private readonly Func<TransferEngine?> _engineProvider;
    private readonly CancellationTokenSource _cts = new();

    private Task? _loop;
    private string _lastSignature = "";

    /// <summary>
    /// Takes a peer provider rather than Discovery itself, so the comparison can be exercised
    /// against a real socket without opening a broadcast listener - which is the one thing that
    /// would make running the tests pop a Windows firewall prompt.
    /// </summary>
    public PeerWatcher(AppConfig config, Func<IReadOnlyCollection<LanPeer>> peerProvider, Func<TransferEngine?> engineProvider)
    {
        _config = config;
        _peerProvider = peerProvider;
        _engineProvider = engineProvider;
        _client = new LanClient(config);
    }

    public PeerWatcher(AppConfig config, Discovery discovery, Func<TransferEngine?> engineProvider)
        : this(config, () => discovery.Peers, engineProvider) { }

    public string PersonName { get; set; } = "";
    public int ReplyPort { get; set; } = LanProtocol.DefaultPort;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Latest read of what is where. Empty when the other PC is not reachable.</summary>
    public IReadOnlyList<PeerNews> News { get; private set; } = Array.Empty<PeerNews>();

    /// <summary>Raised only when the picture actually changes, so the UI does not flicker.</summary>
    public event Action<IReadOnlyList<PeerNews>>? NewsChanged;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    public void Stop()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Asks the other PC to send a save over. It lands in the inbox, never straight into the game.</summary>
    public Task<bool> FetchAsync(PeerNews news, CancellationToken ct = default)
        => _client.RequestSendAsync(news.Peer, news.Remote.SaveId, ReplyPort, PersonName, ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
            {
                // The other PC went away mid-question. Nothing to do but ask again later.
            }

            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        var engine = _engineProvider();
        if (engine is null) { Publish(Array.Empty<PeerNews>()); return; }

        var peers = _peerProvider().Where(p => p.IsFresh).ToList();
        if (peers.Count == 0) { Publish(Array.Empty<PeerNews>()); return; }

        var locals = SaveDiscovery.Enumerate(engine.Location, measure: true, ct);
        var results = new List<PeerNews>();

        foreach (var peer in peers)
        {
            ct.ThrowIfCancellationRequested();

            var remoteSaves = await _client.ListSavesAsync(peer, PersonName, ct).ConfigureAwait(false);
            if (remoteSaves is null) continue;

            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var remote in remoteSaves)
            {
                var local = Match(locals, remote);
                if (local is not null) matched.Add(local.Folder);
                results.Add(Compare(peer, remote, local));
            }

            // Saves that exist here and not over there. Walking only the far side's list would
            // miss them entirely, which would leave "send my saves" with nothing to offer on the
            // very first setup - the one time everything needs sending.
            foreach (var local in locals)
            {
                if (matched.Contains(local.Folder)) continue;

                results.Add(new PeerNews
                {
                    Peer = peer,
                    Remote = new PeerSave
                    {
                        SaveId = local.Passport?.SaveId ?? "",
                        World = local.World,
                        SaveName = local.SaveName,
                        Passport = local.Passport,
                        SizeBytes = local.SizeBytes,
                        LastPlayedAt = local.LastWriteUtc,
                    },
                    Local = local,
                    Relation = Relation.NoLocal,
                    Direction = SyncDirection.ToStick,
                    Summary = $"{peer.Label} does not have this save yet.",
                });
            }
        }

        Publish(results);
    }

    private static SaveSlot? Match(List<SaveSlot> locals, PeerSave remote)
    {
        var byId = locals.FirstOrDefault(s =>
            s.Passport is not null
            && !string.IsNullOrWhiteSpace(remote.SaveId)
            && string.Equals(s.Passport.SaveId, remote.SaveId, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;

        return locals.FirstOrDefault(s =>
            string.Equals(s.World, remote.World, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.SaveName, remote.SaveName, StringComparison.OrdinalIgnoreCase));
    }

    private static PeerNews Compare(LanPeer peer, PeerSave remote, SaveSlot? local)
    {
        // Mirrors the USB stick logic exactly, so the network path cannot drift into a different
        // answer from the one the user would have got carrying a stick across the room.
        bool localDirty = local is not null && TransferEngine.LooksChangedSinceCommit(local);

        if (remote.Passport is null)
        {
            return new PeerNews
            {
                Peer = peer, Remote = remote, Local = local,
                Relation = Relation.Unregistered,
                Direction = SyncDirection.UpToDate,
                Summary = $"{peer.Label} has a save that has never been copied anywhere.",
            };
        }

        if (local is null)
        {
            return new PeerNews
            {
                Peer = peer, Remote = remote, Local = null,
                Relation = Relation.NoLocal,
                Direction = SyncDirection.ToPc,
                Summary = $"{peer.Label} has a save this PC does not have.",
            };
        }

        var relation = SaveDiscovery.Classify(local, remote.Passport);

        var (direction, summary) = relation switch
        {
            Relation.Identical when remote.PlayedSinceLastCopy && localDirty =>
                (SyncDirection.Conflict, $"Both this PC and {peer.Label} have been played since they last matched."),
            Relation.Identical when remote.PlayedSinceLastCopy =>
                (SyncDirection.ToPc, $"{peer.Label} has been played since these last matched."),
            Relation.Identical =>
                (SyncDirection.UpToDate, "Both PCs match."),

            Relation.FastForward when localDirty =>
                (SyncDirection.Conflict, $"{peer.Label} is newer, but this PC was played too."),
            Relation.FastForward =>
                (SyncDirection.ToPc, $"{peer.Label} has a newer copy, played {Ago(remote.LastPlayedAt)}."),

            Relation.Stale when remote.PlayedSinceLastCopy =>
                (SyncDirection.Conflict, $"This PC is ahead, but {peer.Label} has been played too."),
            Relation.Stale =>
                (SyncDirection.ToStick, "This PC has the newer copy."),

            Relation.Diverged =>
                (SyncDirection.Conflict, $"Both this PC and {peer.Label} were played since they last matched."),

            _ => (SyncDirection.Conflict, $"This PC and {peer.Label} both have a save with this name."),
        };

        return new PeerNews
        {
            Peer = peer, Remote = remote, Local = local,
            Relation = relation, Direction = direction, Summary = summary,
        };
    }

    private static string Ago(DateTimeOffset when)
    {
        if (when == DateTimeOffset.MinValue) return "recently";
        var span = DateTimeOffset.UtcNow - when.ToUniversalTime();
        if (span < TimeSpan.FromMinutes(2)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} minutes ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} hours ago";
        return $"{(int)span.TotalDays} days ago";
    }

    private void Publish(IReadOnlyList<PeerNews> results)
    {
        var signature = string.Join("|", results.Select(r =>
            $"{r.Peer.MachineId}:{r.Remote.SaveId}:{r.Remote.Passport?.VersionId}:{r.Direction}"));

        News = results;
        if (signature == _lastSignature) return;

        _lastSignature = signature;
        NewsChanged?.Invoke(results);
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
