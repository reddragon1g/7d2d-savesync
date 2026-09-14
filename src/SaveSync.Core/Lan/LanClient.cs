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

/// <summary>What another PC says about itself when asked.</summary>
public sealed class PeerReport
{
    public required string Label { get; init; }
    public required string Text { get; init; }
    public string ToolVersion { get; init; } = "";
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
    /// <summary>
    /// Asks another PC what it has been doing, and gets its log back.
    ///
    /// The whole reason this exists: a machine you cannot walk over to can be asked to account for
    /// itself. Read-only and harmless - the same text somebody sitting at that PC can open from
    /// its own window.
    /// </summary>
    public async Task<PeerReport?> GetLogAsync(
        LanPeer peer, string senderName, CancellationToken ct = default)
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
                Op = "get-log",
                Secret = secret,
                MachineId = _config.MachineId,
                DisplayName = _config.DisplayName,
                SenderName = senderName,
            }, ct: ct).ConfigureAwait(false);

            var response = await LanProtocol.ReadHeaderAsync<LanResponse>(stream, ct).ConfigureAwait(false);
            if (response is null || !response.Ok) return null;

            return new PeerReport
            {
                Label = response.LogLabel ?? peer.Label,
                Text = response.LogText ?? "",
                ToolVersion = response.ToolVersion ?? "",
            };
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Offers this program to another PC and, if it says yes, sends it.
    ///
    /// Two steps on purpose. The offer names the version and the checksum, so a machine that does
    /// not want it - or already has it, or has not been told to accept updates at all - says so
    /// before sixty megabytes cross the network. The answer is the far side's to give.
    /// </summary>
    public async Task<(bool Sent, string Message)> PushUpdateAsync(
        LanPeer peer, string exePath, string senderName, CancellationToken ct = default)
    {
        if (!File.Exists(exePath)) return (false, "The program file could not be found.");

        if (!await EnsurePairedAsync(peer, senderName, ct).ConfigureAwait(false))
            return (false, $"Could not reach {peer.Label}.");

        var secret = _config.FindPeer(peer.MachineId)?.Secret;
        if (secret is null) return (false, $"Not paired with {peer.Label}.");

        string sha;
        long size;
        try
        {
            sha = RemoteUpdate.Sha256Of(exePath);
            size = new FileInfo(exePath).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, "Could not read the program file: " + e.Message);
        }

        // The version of the FILE, not of whoever is sending it.
        var offered = RemoteUpdate.VersionOf(exePath);
        if (offered <= new Version(0, 0))
            return (false, "That program file does not say what version it is.");

        var version = offered.ToString();

        try
        {
            // Ask first.
            using (var ask = await ConnectAsync(peer.Address, peer.Port, ct).ConfigureAwait(false))
            await using (var askStream = ask.GetStream())
            {
                await LanProtocol.WriteMessageAsync(askStream, new LanRequest
                {
                    Op = "update-offer",
                    Secret = secret,
                    MachineId = _config.MachineId,
                    DisplayName = _config.DisplayName,
                    SenderName = senderName,
                    OfferedVersion = version,
                    OfferedSha256 = sha,
                }, ct: ct).ConfigureAwait(false);

                var answer = await LanProtocol.ReadHeaderAsync<LanResponse>(askStream, ct).ConfigureAwait(false);
                if (answer is null) return (false, $"{peer.Label} did not answer.");
                if (!answer.Ok) return (false, answer.Error ?? "It was refused.");
            }

            // Then send.
            using var client = await ConnectAsync(peer.Address, peer.Port, ct).ConfigureAwait(false);
            await using var stream = client.GetStream();
            await using var file = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            await LanProtocol.WriteMessageAsync(stream, new LanRequest
            {
                Op = "update-file",
                Secret = secret,
                MachineId = _config.MachineId,
                DisplayName = _config.DisplayName,
                SenderName = senderName,
                OfferedVersion = version,
                OfferedSha256 = sha,
                BodyBytes = size,
            }, file, size, ct).ConfigureAwait(false);

            var done = await LanProtocol.ReadHeaderAsync<LanResponse>(stream, ct).ConfigureAwait(false);
            if (done is null || !done.Ok) return (false, done?.Error ?? "It did not arrive.");

            return (true, done.Message ?? $"{peer.Label} has the update.");
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            return (false, e.Message);
        }
    }

    /// <summary>Asks another PC to rename one of its saves. Nothing is copied or deleted.</summary>
    public async Task<(bool Ok, string Message)> RenameSaveAsync(
        LanPeer peer, string world, string saveName, string newName, string senderName,
        CancellationToken ct = default)
    {
        if (!await EnsurePairedAsync(peer, senderName, ct).ConfigureAwait(false))
            return (false, $"Could not reach {peer.Label}.");

        var secret = _config.FindPeer(peer.MachineId)?.Secret;
        if (secret is null) return (false, $"Not paired with {peer.Label}.");

        try
        {
            using var client = await ConnectAsync(peer.Address, peer.Port, ct).ConfigureAwait(false);
            await using var stream = client.GetStream();

            await LanProtocol.WriteMessageAsync(stream, new LanRequest
            {
                Op = "rename-save",
                Secret = secret,
                MachineId = _config.MachineId,
                DisplayName = _config.DisplayName,
                SenderName = senderName,
                World = world,
                SaveName = saveName,
                InstallAsName = newName,
            }, ct: ct).ConfigureAwait(false);

            var response = await LanProtocol.ReadHeaderAsync<LanResponse>(stream, ct).ConfigureAwait(false);
            if (response is null) return (false, $"{peer.Label} did not answer.");
            return (response.Ok, response.Ok ? response.Message ?? "Done." : response.Error ?? "Refused.");
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            return (false, e.Message);
        }
    }

    /// <summary>Asks another PC to hand over to its installed copy, so an update takes effect.</summary>
    public async Task<(bool Ok, string Message)> RestartAsync(
        LanPeer peer, string senderName, CancellationToken ct = default)
    {
        var response = await SimpleAsync(peer, "restart", senderName, null, null, ct).ConfigureAwait(false);
        if (response is null) return (false, $"{peer.Label} did not answer.");
        return (response.Ok, response.Ok ? response.Message ?? "Restarting." : response.Error ?? "Refused.");
    }

    /// <summary>
    /// Asks another PC to close the game, or to start it - optionally straight into a named save.
    ///
    /// The save is named by world and name rather than by SaveId on purpose: the other machine
    /// matches it against its own folders, so a name that is not there is refused there rather
    /// than guessed at here.
    /// </summary>
    public async Task<(bool Ok, string Message)> GameAsync(
        LanPeer peer, bool start, string senderName, CancellationToken ct = default,
        string? world = null, string? saveName = null, string? tune = null)
    {
        var response = await SimpleAsync(peer, start ? "game-start" : "game-stop", senderName, null, null, ct,
                                         world, saveName, tune)
            .ConfigureAwait(false);

        if (response is null) return (false, $"{peer.Label} did not answer.");
        return (response.Ok, response.Ok ? response.Message ?? "Done." : response.Error ?? "Refused.");
    }

    /// <summary>Asks another PC to give back the spawn-screen setting a remote launch borrowed.</summary>
    public async Task<(bool Ok, string Message)> SpawnPrefResetAsync(
        LanPeer peer, string senderName, CancellationToken ct = default)
    {
        var response = await SimpleAsync(peer, "spawn-pref-reset", senderName, null, null, ct)
            .ConfigureAwait(false);

        if (response is null) return (false, $"{peer.Label} did not answer.");
        return (response.Ok, response.Ok ? response.Message ?? "Done." : response.Error ?? "Refused.");
    }

    /// <summary>Points another PC's game at one graphics chip or the other.</summary>
    public async Task<(bool Ok, string Message)> GpuChoiceAsync(
        LanPeer peer, string which, string senderName, CancellationToken ct = default)
    {
        var response = await SimpleAsync(peer, "gpu-choice", senderName, null, null, ct, gpu: which)
            .ConfigureAwait(false);

        if (response is null) return (false, $"{peer.Label} did not answer.");
        return (response.Ok, response.Ok ? response.Message ?? "Done." : response.Error ?? "Refused.");
    }

    /// <summary>What another PC is, and how the game is running on it.</summary>
    public async Task<MachineReport?> MachineAsync(LanPeer peer, string senderName, CancellationToken ct = default)
    {
        var response = await SimpleAsync(peer, "get-machine", senderName, null, null, ct).ConfigureAwait(false);
        if (response is null || !response.Ok || response.InboxJson is null) return null;
        return Json.Read<MachineReport>(response.InboxJson);
    }

    /// <summary>What is waiting for a person on another PC.</summary>
    public async Task<List<WaitingSave>?> InboxAsync(LanPeer peer, string senderName, CancellationToken ct = default)
    {
        var response = await SimpleAsync(peer, "inbox-list", senderName, null, null, ct).ConfigureAwait(false);
        if (response is null || !response.Ok || response.InboxJson is null) return null;
        return Json.Read<List<WaitingSave>>(response.InboxJson);
    }

    /// <summary>
    /// Asks another PC to install a waiting save beside what it already has, under a new name.
    ///
    /// The only decision that can be made remotely, because it is the only one that cannot cost
    /// anything: nothing on that machine is replaced, renamed or removed.
    /// </summary>
    public async Task<(bool Ok, string Message)> KeepBothAsync(
        LanPeer peer, string inboxId, string? installAs, string senderName, CancellationToken ct = default)
    {
        var response = await SimpleAsync(peer, "inbox-keep-both", senderName, inboxId, installAs, ct)
            .ConfigureAwait(false);

        if (response is null) return (false, $"{peer.Label} did not answer.");
        return (response.Ok, response.Ok ? response.Message ?? "Done." : response.Error ?? "Refused.");
    }

    /// <summary>One request, one answer, no body. Shared by the small operations.</summary>
    private async Task<LanResponse?> SimpleAsync(
        LanPeer peer, string op, string senderName, string? inboxId, string? installAs, CancellationToken ct,
        string? world = null, string? saveName = null, string? tune = null, string? gpu = null)
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
                Op = op,
                Secret = secret,
                MachineId = _config.MachineId,
                DisplayName = _config.DisplayName,
                SenderName = senderName,
                InboxId = inboxId,
                InstallAsName = installAs,
                World = world,
                SaveName = saveName,
                Tune = tune,
                Gpu = gpu,
            }, ct: ct).ConfigureAwait(false);

            return await LanProtocol.ReadHeaderAsync<LanResponse>(stream, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            return null;
        }
    }

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
