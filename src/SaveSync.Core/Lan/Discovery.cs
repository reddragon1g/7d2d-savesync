using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SaveSync.Core.Lan;

/// <summary>
/// Finds the other PC on the same network without anyone typing an address.
///
/// Each copy shouts a short message every couple of seconds and listens for the others. Broadcasts
/// go to every network the machine is actually on, because a PC with a virtual adapter or two will
/// otherwise shout only into the wrong one.
/// </summary>
public sealed class Discovery : IDisposable
{
    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<string, LanPeer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();

    private UdpClient? _listener;
    private Task? _listenTask;
    private Task? _announceTask;

    public Discovery(AppConfig config) => _config = config;

    /// <summary>Person using this copy, included so the far end can say "Ryan" rather than a PC name.</summary>
    public string PersonName { get; set; } = "";

    /// <summary>Port the local server is actually listening on.</summary>
    public int ServerPort { get; set; } = LanProtocol.DefaultPort;

    public string? StartFailure { get; private set; }

    public event Action<LanPeer>? PeerSeen;

    public IReadOnlyCollection<LanPeer> Peers => _peers.Values.Where(p => p.IsFresh).ToList();

    public void Start()
    {
        if (_listener is not null) return;

        try
        {
            var listener = new UdpClient(AddressFamily.InterNetwork);
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, LanProtocol.DiscoveryPort));
            listener.EnableBroadcast = true;
            _listener = listener;
            StartFailure = null;
        }
        catch (SocketException ex)
        {
            StartFailure = ex.Message;
            return;
        }

        _listenTask = Task.Run(() => ListenAsync(_cts.Token));
        _announceTask = Task.Run(() => AnnounceAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Close(); } catch (SocketException) { }
        _listener = null;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await listener.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            Beacon? beacon;
            try { beacon = Json.Read<Beacon>(Encoding.UTF8.GetString(result.Buffer)); }
            catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException) { continue; }

            if (beacon is null || beacon.Version != LanProtocol.Version) continue;
            if (string.IsNullOrWhiteSpace(beacon.MachineId)) continue;
            if (string.Equals(beacon.MachineId, _config.MachineId, StringComparison.OrdinalIgnoreCase)) continue;

            var peer = new LanPeer
            {
                MachineId = beacon.MachineId,
                DisplayName = string.IsNullOrWhiteSpace(beacon.DisplayName) ? "the other PC" : beacon.DisplayName,
                PersonName = beacon.PersonName,
                Address = result.RemoteEndPoint.Address.ToString(),
                Port = beacon.Port <= 0 ? LanProtocol.DefaultPort : beacon.Port,
                LastSeen = DateTimeOffset.UtcNow,
            };

            bool isNew = !_peers.ContainsKey(peer.MachineId);
            _peers[peer.MachineId] = peer;
            if (isNew) PeerSeen?.Invoke(peer);
        }
    }

    private async Task AnnounceAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Announce(); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Announce()
    {
        var payload = Encoding.UTF8.GetBytes(Json.Write(new Beacon
        {
            MachineId = _config.MachineId,
            DisplayName = _config.DisplayName,
            PersonName = PersonName,
            Port = ServerPort,
        }));

        using var sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };

        foreach (var target in BroadcastTargets())
        {
            try { sender.Send(payload, payload.Length, new IPEndPoint(target, LanProtocol.DiscoveryPort)); }
            catch (SocketException) { /* that adapter is not usable; try the rest */ }
        }
    }

    /// <summary>
    /// The broadcast address of every live IPv4 network, plus the global one as a fallback. A
    /// machine with a VPN or a virtual switch has several, and guessing a single one is how
    /// discovery silently fails on exactly the PC you cannot test on.
    /// </summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        yield return IPAddress.Broadcast;

        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            UnicastIPAddressInformationCollection addresses;
            try { addresses = nic.GetIPProperties().UnicastAddresses; }
            catch (NetworkInformationException) { continue; }

            foreach (var entry in addresses)
            {
                if (entry.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (entry.IPv4Mask is null) continue;

                var address = entry.Address.GetAddressBytes();
                var mask = entry.IPv4Mask.GetAddressBytes();
                if (address.Length != 4 || mask.Length != 4) continue;

                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++) broadcast[i] = (byte)(address[i] | ~mask[i]);
                yield return new IPAddress(broadcast);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
