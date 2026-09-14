using System.Net;
using SaveSync.Core;
using SaveSync.Core.Lan;
using Xunit;

namespace SaveSync.Core.Tests;

/// <summary>
/// Real transfers between two engines over a real TCP connection on loopback.
///
/// This is the part that has to be trusted without a second PC to hand, so the tests drive the
/// actual sockets rather than mocking them: bytes are framed, streamed, hashed on arrival, and
/// committed by the receiving engine exactly as they would be between two machines.
/// Loopback is used deliberately - Windows never asks about it, so running the suite cannot
/// trigger a firewall prompt.
/// </summary>
public class LanTests : IDisposable
{
    private readonly TestEnv _sender = new();
    private readonly TestEnv _receiver = new();
    private readonly string _outbox;
    private LanServer? _server;

    public LanTests()
    {
        _outbox = Path.Combine(Path.GetTempPath(), "savesync-tests", "outbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outbox);

        _sender.Config.MachineId = "sender000001";
        _sender.Config.DisplayName = "DESKTOP";
        _receiver.Config.MachineId = "receiver0001";
        _receiver.Config.DisplayName = "LAPTOP";
    }

    public void Dispose()
    {
        _server?.Dispose();
        _sender.Dispose();
        _receiver.Dispose();
        try { PathUtil.DeleteTree(_outbox); } catch (IOException) { }
    }

    private LanPeer StartReceiver()
    {
        var engine = _receiver.NewEngine();
        _server = new LanServer(_receiver.Config, () => engine) { BindAddress = IPAddress.Loopback };
        _server.Start();

        Assert.True(_server.Running, $"listener did not start: {_server.StartFailure}");

        return new LanPeer
        {
            MachineId = _receiver.Config.MachineId,
            DisplayName = _receiver.Config.DisplayName,
            Address = "127.0.0.1",
            Port = _server.Port,
            PersonName = "Sarah",
            LastSeen = DateTimeOffset.UtcNow,
        };
    }

    private string BuildPackage(string world = "Navezgane", string save = "My Game")
    {
        var engine = _sender.NewEngine();
        engine.Identity = "Ryan";
        return engine.Export(_sender.Slot(world, save), _outbox).PackageDir;
    }

    // ------------------------------------------------------------------ the main case

    [Fact]
    public async Task Save_transfers_over_the_network_and_is_applied()
    {
        _sender.MakeSave();
        var peer = StartReceiver();
        var package = BuildPackage();

        var result = await new LanClient(_sender.Config).SendPackageAsync(peer, package, "Ryan");

        Assert.True(result.Sent, result.Error);
        Assert.True(result.AppliedRemotely, result.Message);

        var landed = _receiver.Slot();
        var manifest = Json.ReadFile<Manifest>(PackageLayout.Manifest(package))!;
        Assert.Empty(manifest.Verify(landed.Folder));
    }

    /// <summary>
    /// The thing that must not be lost in transit: the world itself and every player's character.
    /// </summary>
    [Fact]
    public async Task Transfer_carries_the_world_and_every_player()
    {
        var source = _sender.MakeSave();
        var expectedPlayers = Directory.GetFiles(Path.Combine(source, "Player"), "*.ttp")
            .Select(Path.GetFileName).OrderBy(x => x).ToArray();
        var expectedRegions = Directory.GetFiles(Path.Combine(source, "Region"))
            .Select(Path.GetFileName).OrderBy(x => x).ToArray();

        Assert.Equal(2, expectedPlayers.Length);
        Assert.NotEmpty(expectedRegions);

        var peer = StartReceiver();
        var result = await new LanClient(_sender.Config).SendPackageAsync(peer, BuildPackage(), "Ryan");
        Assert.True(result.Sent, result.Error);

        var landedDir = _receiver.Slot().Folder;

        Assert.Equal(expectedPlayers,
            Directory.GetFiles(Path.Combine(landedDir, "Player"), "*.ttp").Select(Path.GetFileName).OrderBy(x => x));
        Assert.Equal(expectedRegions,
            Directory.GetFiles(Path.Combine(landedDir, "Region")).Select(Path.GetFileName).OrderBy(x => x));

        // Byte-identical, not merely present.
        foreach (var name in expectedPlayers)
        {
            Assert.Equal(
                Manifest.HashFile(Path.Combine(source, "Player", name!)),
                Manifest.HashFile(Path.Combine(landedDir, "Player", name!)));
        }
    }

    /// <summary>
    /// A save that arrives over the network must be indistinguishable from one carried on a stick,
    /// timestamps included. Writing received files with a fresh timestamp made every transfer look
    /// like it had just been played, which invented conflicts out of nothing.
    /// </summary>
    [Fact]
    public async Task Transferred_files_keep_their_original_timestamps()
    {
        var source = _sender.MakeSave();
        var peer = StartReceiver();

        var before = Directory.GetFiles(Path.Combine(source, "Region"))
            .ToDictionary(f => Path.GetFileName(f), f => new FileInfo(f).LastWriteTimeUtc);

        Assert.True((await new LanClient(_sender.Config).SendPackageAsync(peer, BuildPackage(), "Ryan")).Sent);

        var landed = _receiver.Slot().Folder;
        foreach (var (name, when) in before)
        {
            Assert.Equal(when, new FileInfo(Path.Combine(landed, "Region", name)).LastWriteTimeUtc);
        }

        Assert.False(TransferEngine.LooksChangedSinceCommit(_receiver.Slot()),
            "a save that has only just arrived has not been played");
    }

    [Fact]
    public async Task Custom_world_map_data_travels_too()
    {
        _sender.MakeGeneratedWorld("Wasteland Valley");
        _sender.MakeSave(world: "Wasteland Valley", saveName: "Day 1");

        var peer = StartReceiver();
        var package = BuildPackage("Wasteland Valley", "Day 1");

        var result = await new LanClient(_sender.Config).SendPackageAsync(peer, package, "Ryan");

        Assert.True(result.Sent, result.Error);
        Assert.True(Directory.Exists(Path.Combine(_receiver.Location.GeneratedWorldsDir, "Wasteland Valley")),
            "the custom map has to arrive or the save will not load");
    }

    [Fact]
    public async Task Round_trip_both_ways_stays_consistent()
    {
        _sender.MakeSave();
        var peer = StartReceiver();
        var client = new LanClient(_sender.Config);

        Assert.True((await client.SendPackageAsync(peer, BuildPackage(), "Ryan")).Sent);

        // The receiver plays, then sends it back the other way.
        _receiver.Play(_receiver.Slot().Folder, seed: 77);
        var backOutbox = Path.Combine(_outbox, "back");
        Directory.CreateDirectory(backOutbox);

        var receiverEngine = _receiver.NewEngine();
        receiverEngine.Identity = "Sarah";
        var backPackage = receiverEngine.Export(_receiver.Slot(), backOutbox).PackageDir;

        var senderEngine = _sender.NewEngine();
        var senderServer = new LanServer(_sender.Config, () => senderEngine) { BindAddress = IPAddress.Loopback };
        senderServer.Start();

        try
        {
            var backPeer = new LanPeer
            {
                MachineId = _sender.Config.MachineId,
                DisplayName = _sender.Config.DisplayName,
                Address = "127.0.0.1",
                Port = senderServer.Port,
                LastSeen = DateTimeOffset.UtcNow,
            };

            var result = await new LanClient(_receiver.Config).SendPackageAsync(backPeer, backPackage, "Sarah");

            Assert.True(result.Sent, result.Error);
            Assert.True(result.AppliedRemotely, result.Message);

            var manifest = Json.ReadFile<Manifest>(PackageLayout.Manifest(backPackage))!;
            Assert.Empty(manifest.Verify(_sender.Slot().Folder));
        }
        finally { senderServer.Dispose(); }
    }

    // ------------------------------------------------------------------ refusing to lose progress

    /// <summary>
    /// A remote machine must never be able to talk this one into overwriting newer progress. When
    /// both sides were played, the transfer is kept but not applied.
    /// </summary>
    [Fact]
    public async Task Divergent_transfer_is_held_not_applied()
    {
        _sender.MakeSave();
        var peer = StartReceiver();
        var client = new LanClient(_sender.Config);

        Assert.True((await client.SendPackageAsync(peer, BuildPackage(), "Ryan")).Sent);

        // Both sides play on from the same point.
        _receiver.Play(_receiver.Slot().Folder, seed: 3);
        var receiverState = Manifest.Build(_receiver.Slot().Folder);

        _sender.Play(_sender.Slot().Folder, seed: 4);

        var result = await client.SendPackageAsync(peer, BuildPackage(), "Ryan");

        Assert.True(result.Sent, result.Error);
        Assert.False(result.AppliedRemotely);
        Assert.Contains("needs someone", result.Message);

        // The receiver's own copy is exactly as it was.
        Assert.Empty(receiverState.Verify(_receiver.Slot().Folder));

        // ...and the transfer is waiting for a person, not thrown away.
        var waiting = Inbox.List(new Workspace(_receiver.Location.UserDataRoot));
        Assert.Single(waiting);
    }

    [Fact]
    public async Task An_existing_unlinked_save_is_never_overwritten_remotely()
    {
        _sender.MakeSave();
        var existing = _receiver.MakeSave(seed: 91);
        var before = Manifest.Build(existing);

        var peer = StartReceiver();
        var result = await new LanClient(_sender.Config).SendPackageAsync(peer, BuildPackage(), "Ryan");

        Assert.True(result.Sent, result.Error);
        Assert.False(result.AppliedRemotely);
        Assert.Empty(before.Verify(existing));
    }

    [Fact]
    public async Task Transfer_is_refused_while_the_game_is_running_on_the_far_side()
    {
        _sender.MakeSave();
        var peer = StartReceiver();
        var package = BuildPackage();

        try
        {
            TransferEngine.IsGameRunningProbe = () => true;
            var result = await new LanClient(_sender.Config).SendPackageAsync(peer, package, "Ryan");

            Assert.False(result.Sent);
            Assert.Contains("running", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally { TransferEngine.IsGameRunningProbe = () => false; }
    }

    // ------------------------------------------------------------------ hostile input

    [Fact]
    public async Task Unknown_machine_cannot_push_without_being_introduced()
    {
        _sender.MakeSave();
        var peer = StartReceiver();
        var package = BuildPackage();

        // A machine that never paired, presenting a made-up secret.
        var stranger = new AppConfig { MachineId = "stranger0001", DisplayName = "SOMEONE-ELSE" };
        stranger.RememberPeer(peer.MachineId, peer.DisplayName, peer.Address).Secret = "not-the-real-secret";

        var result = await new LanClient(stranger).SendPackageAsync(peer, package, "Nobody");

        Assert.False(result.Sent);
        Assert.Empty(SaveDiscovery.Enumerate(_receiver.Location));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("C:\\Windows\\evil.txt")]
    [InlineData("sub/../../escape.txt")]
    public void Paths_that_climb_out_of_the_inbox_are_refused(string path)
    {
        var slot = Path.Combine(_receiver.Root, "slot");
        Directory.CreateDirectory(slot);
        Assert.Throws<InvalidDataException>(() => Inbox.ResolveInside(slot, path));
    }

    [Fact]
    public void Ordinary_nested_paths_resolve_inside_the_inbox()
    {
        var slot = Path.Combine(_receiver.Root, "slot");
        Directory.CreateDirectory(slot);

        var resolved = Inbox.ResolveInside(slot, "payload/Region/r.0.0.7rg");
        Assert.True(PathUtil.IsUnder(resolved, slot));
    }

    [Fact]
    public async Task Hello_reports_who_is_there()
    {
        var peer = StartReceiver();
        var response = await new LanClient(_sender.Config).HelloAsync(peer.Address, peer.Port);

        Assert.NotNull(response);
        Assert.True(response!.Ok);
        Assert.Equal(_receiver.Config.MachineId, response.MachineId);
    }

    [Fact]
    public async Task Nothing_answers_on_a_closed_port()
    {
        var response = await new LanClient(_sender.Config).HelloAsync("127.0.0.1", 47999);
        Assert.Null(response);
    }
}
