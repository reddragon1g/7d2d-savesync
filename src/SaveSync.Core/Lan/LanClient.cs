using System.Net.Sockets;

namespace SaveSync.Core.Lan;

public sealed class LanPeer
{
    public required string MachineId { get; init; }
    public required string DisplayName { get; init; }
    public required string Address { get; init; }
    public int Port { get; init; } = LanProtocol.DefaultPort;
    public string PersonName { get; init; } = "";
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>Who to say is on the other end, preferring the person over the machine.</summary>
    public string Label => string.IsNullOrWhiteSpace(PersonName) ? DisplayName : PersonName;

    public bool IsFresh => DateTimeOffset.UtcNow - LastSeen < TimeSpan.FromSeconds(12);
}

public sealed class SendResult
{
    public bool Sent { get; init; }
    public bool AppliedRemotely { get; init; }
    public string Message { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>
/// Sends a package to the other PC.
///
/// Each file is hashed before it leaves and checked again on arrival, and the transfer is only
/// declared complete once the far side has verified the whole manifest. A dropped connection at
/// any point leaves the other machine's saves untouched, because nothing is applied until commit.
/// </summary>
public sealed class LanClient
{
    private readonly AppConfig _config;

    public LanClient(AppConfig config) => _config = config;

    /// <summary>Identity of whoever answers, or null when nobody does.</summary>
    public async Task<LanResponse?> HelloAsync(string address, int port, CancellationToken ct = default)
    {
        try
        {
            using var client = await ConnectAsync(address, port, ct).ConfigureAwait(false);
            await using var stream = client.GetStream();

            await LanProtocol.WriteMessageAsync(stream, new LanRequest
            {
                Op = "hello",
                MachineId = _config.MachineId,
                DisplayName = _config.DisplayName,
            }, ct: ct).ConfigureAwait(false);

            return await LanProtocol.ReadHeaderAsync<LanResponse>(stream, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes sure the two machines share a secret. Both sides do this on their own; nobody is
    /// asked anything.
    /// </summary>
    public async Task<bool> EnsurePairedAsync(LanPeer peer, string senderName, CancellationToken ct = default)
    {
        var existing = _config.FindPeer(peer.MachineId);
        if (existing?.IsPaired == true) return true;

        try
        {
            using var client = await ConnectAsync(peer.Address, peer.Port, ct).ConfigureAwait(false);
            await using var stream = client.GetStream();

            await LanProtocol.WriteMessageAsync(stream, new LanRequest
            {
                Op = "pair",
                MachineId = _config.MachineId,
                DisplayName = _config.DisplayName,
                SenderName = senderName,
            }, ct: ct).ConfigureAwait(false);

            var response = await LanProtocol.ReadHeaderAsync<LanResponse>(stream, ct).ConfigureAwait(false);
            if (response is null || !response.Ok || string.IsNullOrWhiteSpace(response.Secret)) return false;

            var stored = _config.RememberPeer(peer.MachineId, peer.DisplayName, peer.Address);
            stored.Secret = response.Secret;
            try { _config.Save(); } catch (IOException) { }
            return true;
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Asks what the other PC is holding, without moving any save data.</summary>
    public async Task<List<PeerSave>?> ListSavesAsync(LanPeer peer, string senderName, CancellationToken ct = default)
    {
        if (!await EnsurePairedAsync(peer, senderName, ct).ConfigureAwait(false)) return null;
        var secret = _config.FindPeer(peer.MachineId)?.Secret;
        if (secret is null) return null;

        try
        {
            using var client = await ConnectAsync(peer.Address, peer.Port, ct).ConfigureAwait(false);
            await using var stream = client.GetStream();

            await LanProtocol.WriteMessageAsync(stream, new LanRequest
            {
                Op = "list-saves",
                Secret = secret,
                MachineId = _config.MachineId,
                DisplayName = _config.DisplayName,
                SenderName = senderName,
            }, ct: ct).ConfigureAwait(false);

            var response = await LanProtocol.ReadHeaderAsync<LanResponse>(stream, ct).ConfigureAwait(false);
            if (response is null || !response.Ok || response.SavesJson is null) return null;

            return Json.Read<List<PeerSave>>(response.SavesJson);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException
                                  or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the other PC to package a save and send it here. It reads on that side and writes only
    /// into this machine's inbox, so nothing is at risk on either end.
    /// </summary>
    public async Task<bool> RequestSendAsync(
        LanPeer peer, string saveId, int replyPort, string senderName, CancellationToken ct = default,
        string? world = null, string? saveName = null)
    {
        if (!await EnsurePairedAsync(peer, senderName, ct).ConfigureAwait(false)) return false;
        var secret = _config.FindPeer(peer.MachineId)?.Secret;
        if (secret is null) return false;

        try
        {
            using var client = await ConnectAsync(peer.Address, peer.Port, ct).ConfigureAwait(false);
            await using var stream = client.GetStream();

            await LanProtocol.WriteMessageAsync(stream, new LanRequest
            {
                Op = "request-send",
                Secret = secret,
                MachineId = _config.MachineId,
                DisplayName = _config.DisplayName,
                SenderName = senderName,
                SaveId = saveId,
                World = world,
                SaveName = saveName,
                ReplyPort = replyPort,
            }, ct: ct).ConfigureAwait(false);

            var response = await LanProtocol.ReadHeaderAsync<LanResponse>(stream, ct).ConfigureAwait(false);
            return response?.Ok == true;
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Uploads an already-built package folder to the peer.</summary>
    public async Task<SendResult> SendPackageAsync(
        LanPeer peer,
        string packageDir,
        string senderName,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var info = PackageInfo.Load(packageDir);
        var manifestJson = File.Exists(PackageLayout.Manifest(packageDir))
            ? File.ReadAllText(PackageLayout.Manifest(packageDir))
            : null;

        if (info is null || manifestJson is null)
            return new SendResult { Sent = false, Error = "The package is incomplete." };

        if (!await EnsurePairedAsync(peer, senderName, ct).ConfigureAwait(false))
            return new SendResult { Sent = false, Error = $"Could not connect to {peer.Label}." };

        var secret = _config.FindPeer(peer.MachineId)?.Secret;
        if (secret is null) return new SendResult { Sent = false, Error = $"Could not connect to {peer.Label}." };

        var files = CollectFiles(packageDir).ToList();
        long total = files.Sum(f => f.Size);

        string? token = null;
        try
        {
            using var client = await ConnectAsync(peer.Address, peer.Port, ct).ConfigureAwait(false);
            await using var stream = client.GetStream();

            // begin
            await LanProtocol.WriteMessageAsync(stream, new LanRequest
            {
                Op = "push-begin",
                Secret = secret,
                MachineId = _config.MachineId,
                DisplayName = _config.DisplayName,
                SenderName = senderName,
                SaveId = info.Passport.SaveId,
                SaveLabel = $"{info.Passport.SaveName} ({info.Passport.World})",
                FileCount = files.Count,
                TotalBytes = total,
            }, ct: ct).ConfigureAwait(false);

            var begun = await LanProtocol.ReadHeaderAsync<LanResponse>(stream, ct).ConfigureAwait(false);
            if (begun is null || !begun.Ok || begun.Token is null)
                return new SendResult { Sent = false, Error = begun?.Error ?? "The other PC did not accept the transfer." };

            token = begun.Token;

            long done = 0;
            int n = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                await using var body = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1 << 20, useAsync: true);

                await LanProtocol.WriteMessageAsync(stream, new LanRequest
                {
                    Op = "push-file",
                    Secret = secret,
                    MachineId = _config.MachineId,
                    Token = token,
                    Section = file.Section,
                    Path = file.Relative,
                    BodyBytes = file.Size,
                    Sha256 = file.Sha256,
                    MTimeTicks = file.MTimeTicks,
                }, body, file.Size, ct).ConfigureAwait(false);

                var ack = await LanProtocol.ReadHeaderAsync<LanResponse>(stream, ct).ConfigureAwait(false);
                if (ack is null || !ack.Ok)
                    return new SendResult { Sent = false, Error = ack?.Error ?? "The transfer was refused part way through." };

                done += file.Size;
                n++;
                progress?.Report(new ScanProgress(n, files.Count, done, total, file.Relative));
            }

            // commit
            await LanProtocol.WriteMessageAsync(stream, new LanRequest
            {
                Op = "push-commit",
                Secret = secret,
                MachineId = _config.MachineId,
                Token = token,
                PackageInfoJson = File.ReadAllText(PackageLayout.Info(packageDir)),
                ManifestJson = manifestJson,
            }, ct: ct).ConfigureAwait(false);

            var committed = await LanProtocol.ReadHeaderAsync<LanResponse>(stream, ct).ConfigureAwait(false);
            if (committed is null || !committed.Ok)
                return new SendResult { Sent = false, Error = committed?.Error ?? "The other PC could not finish the transfer." };

            token = null; // committed; nothing to abort
            return new SendResult
            {
                Sent = true,
                AppliedRemotely = committed.Applied,
                Message = committed.Message ?? "",
            };
        }
        catch (OperationCanceledException)
        {
            await TryAbortAsync(peer, secret, token).ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (e is SocketException or IOException or InvalidDataException or EndOfStreamException)
        {
            await TryAbortAsync(peer, secret, token).ConfigureAwait(false);
            return new SendResult { Sent = false, Error = $"Lost the connection to {peer.Label}. Nothing was changed there." };
        }
    }

    /// <summary>Best effort tidy-up so an interrupted send does not leave a part-file on the far side.</summary>
    private async Task TryAbortAsync(LanPeer peer, string? secret, string? token)
    {
        if (token is null || secret is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = await ConnectAsync(peer.Address, peer.Port, cts.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await LanProtocol.WriteMessageAsync(stream, new LanRequest
            {
                Op = "push-abort",
                Secret = secret,
                MachineId = _config.MachineId,
                Token = token,
            }, ct: cts.Token).ConfigureAwait(false);
            await LanProtocol.ReadHeaderAsync<LanResponse>(stream, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or InvalidDataException)
        {
            // The far side expires abandoned transfers on its own.
        }
    }

    private static async Task<TcpClient> ConnectAsync(string address, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        LanProtocol.Configure(client);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed record OutgoingFile(
        string FullPath, string Relative, string Section, long Size, string Sha256, long MTimeTicks);

    private static IEnumerable<OutgoingFile> CollectFiles(string packageDir)
    {
        foreach (var section in new[] { PackageLayout.PayloadDir, PackageLayout.WorldDir })
        {
            var root = Path.Combine(packageDir, section);
            if (!Directory.Exists(root)) continue;

            foreach (var full in Manifest.EnumerateFiles(root))
            {
                var rel = Manifest.ToRelative(root, full);
                long size;
                long mtime;
                try
                {
                    var fi = new FileInfo(full);
                    size = fi.Length;
                    mtime = fi.LastWriteTimeUtc.Ticks;
                }
                catch (IOException) { continue; }

                yield return new OutgoingFile(full, rel, section, size, Manifest.HashFile(full), mtime);
            }
        }
    }
}
