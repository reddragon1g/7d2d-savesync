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

    // ------------------------------------------------ asking another PC, and handing it a program

    [Fact]
    public async Task One_PC_can_ask_another_what_it_has_been_doing()
    {
        // The point: a machine in another room can be made to account for itself without walking
        // over to it. Read-only, so any paired peer may ask.
        var ws = new Workspace(_receiver.Location.UserDataRoot);
        try
        {
            ActivityLog.Open(ws, "LAPTOP");
            ActivityLog.Write("applied My Game (Navezgane) v4");

            var peer = StartReceiver();
            var report = await new LanClient(_sender.Config).GetLogAsync(peer, "Ryan");

            Assert.NotNull(report);
            Assert.Contains("applied My Game", report!.Text);
            Assert.False(string.IsNullOrWhiteSpace(report.ToolVersion));
        }
        finally { ActivityLog.Close(); }
    }

    [Fact]
    public async Task A_PC_refuses_a_program_update_unless_it_has_been_told_to_accept_them()
    {
        // Pairing happens automatically and without asking anybody. That is defensible while the
        // worst a peer can do is offer a save this machine then judges for itself - and is not
        // defensible for handing over something that will be run. So this needs its own yes.
        Assert.False(_receiver.Config.AllowRemoteUpdate);

        var exe = AVersionedProgram();
        var peer = StartReceiver();
        var result = await new LanClient(_sender.Config).PushUpdateAsync(peer, exe, "Ryan");

        Assert.False(result.Sent);
        Assert.Contains("not been set to accept", result.Message);
    }

    [Fact]
    public async Task An_older_version_is_refused_even_when_updates_are_allowed()
    {
        // Otherwise a stale copy on somebody's stick could walk a PC backwards.
        _receiver.Config.AllowRemoteUpdate = true;

        var exe = AVersionedProgram();
        var peer = StartReceiver();

        // Both sides are this same assembly, so the offered version equals the installed one -
        // "not newer" is exactly the rule under test.
        var result = await new LanClient(_sender.Config).PushUpdateAsync(peer, exe, "Ryan");

        Assert.False(result.Sent);
        Assert.Contains("already on", result.Message);
    }

    [Fact]
    public async Task A_program_that_does_not_match_its_checksum_is_thrown_away()
    {
        // The bytes are checked before anything is put anywhere it could be run. A file that fails
        // is deleted rather than kept: for an executable there is no sensible "just in case".
        var ws = new Workspace(_receiver.Location.UserDataRoot);
        var wrong = new MemoryStream(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

        var staged = await RemoteUpdate.ReceiveAsync(ws, wrong, 8, "not-the-right-hash");

        Assert.Null(staged);
        Assert.False(File.Exists(Path.Combine(ws.Staging, RemoteUpdate.StagedName)));
    }

    [Fact]
    public async Task A_program_that_matches_its_checksum_is_kept()
    {
        var ws = new Workspace(_receiver.Location.UserDataRoot);
        var payload = new byte[] { 10, 20, 30, 40, 50 };

        var source = Path.Combine(_sender.Root, "good.exe");
        File.WriteAllBytes(source, payload);
        var sha = RemoteUpdate.Sha256Of(source);

        var staged = await RemoteUpdate.ReceiveAsync(ws, new MemoryStream(payload), payload.Length, sha);

        Assert.NotNull(staged);
        Assert.Equal(payload, File.ReadAllBytes(staged!));
    }

    [Fact]
    public async Task A_transfer_that_stops_half_way_is_not_treated_as_a_program()
    {
        // An incomplete program is not a program. The declared length is what makes a short read
        // detectable at all.
        var ws = new Workspace(_receiver.Location.UserDataRoot);
        var half = new MemoryStream(new byte[] { 1, 2, 3 });

        var staged = await RemoteUpdate.ReceiveAsync(ws, half, declaredBytes: 100, "any-hash");

        Assert.Null(staged);
    }

    /// <summary>
    /// A real file that carries a version, for tests about version rules.
    ///
    /// The test assembly itself will do: what matters is that Windows can read a version out of
    /// it, which a handful of made-up bytes cannot - and a file with no version is now refused
    /// before any version rule is reached.
    /// </summary>
    private static string AVersionedProgram() => typeof(LanTests).Assembly.Location;

    [Fact]
    public async Task A_file_that_does_not_say_what_version_it_is_is_refused()
    {
        // The offer describes the FILE, so a file that cannot describe itself cannot be offered.
        // Sending the sender's own version instead was a real fault: a tool built from an older
        // checkout offered "1.4.1" while handing over a 1.5.2 program, and the receiving PC
        // correctly refused the very fix that would have stopped it wedging.
        _receiver.Config.AllowRemoteUpdate = true;

        var nonsense = Path.Combine(_sender.Root, "pretend.exe");
        File.WriteAllBytes(nonsense, new byte[] { 1, 2, 3, 4 });

        var peer = StartReceiver();
        var result = await new LanClient(_sender.Config).PushUpdateAsync(peer, nonsense, "Ryan");

        Assert.False(result.Sent);
        Assert.Contains("does not say what version", result.Message);
    }

    [Fact]
    public void The_version_offered_is_the_files_own_version()
    {
        var mine = RemoteUpdate.VersionOf(AVersionedProgram());

        Assert.NotEqual(new Version(0, 0), mine);
        Assert.Equal(RemoteUpdate.RunningVersion.ToString(3), mine.ToString(3));
    }

    // ------------------------------------------------ deciding from another machine, safely

    [Fact]
    public async Task Keep_both_can_be_asked_for_remotely_and_never_replaces_anything()
    {
        // The one decision that may be made from somewhere else, allowed precisely because of what
        // it cannot do. Every other answer to a clash destroys one of the two saves, and that
        // stays with a person sitting at the machine.
        _sender.MakeSave(saveName: "My Game");
        _receiver.MakeSave(saveName: "My Game", seed: 88);
        var theirs = Manifest.Build(_receiver.Slot(saveName: "My Game").Folder);

        var package = _sender.NewEngine().Export(_sender.Slot(saveName: "My Game"), _outbox).PackageDir;

        var peer = StartReceiver();
        var client = new LanClient(_sender.Config);

        Assert.True((await client.SendPackageAsync(peer, package, "Ryan")).Sent);

        // It could not be applied, so it is waiting.
        var waiting = await client.InboxAsync(peer, "Ryan");
        var item = Assert.Single(waiting!);
        Assert.Equal("My Game", item.SaveName);
        Assert.False(string.IsNullOrWhiteSpace(item.SuggestedName));

        var result = await client.KeepBothAsync(peer, item.Id, "My Game (from Ryan)", "Ryan");
        Assert.True(result.Ok);

        // Installed beside it...
        Assert.NotNull(SaveDiscovery.Find(_receiver.Location, "Navezgane", "My Game (from Ryan)"));

        // ...and the one that was already there is untouched, byte for byte.
        Assert.Empty(theirs.Verify(_receiver.Slot(saveName: "My Game").Folder));
    }

    [Fact]
    public async Task A_remote_keep_both_cannot_be_pointed_at_a_name_that_is_taken()
    {
        // It has exactly one power - adding a save - and it must not be talkable into using that
        // power to land on top of something.
        _sender.MakeSave(saveName: "My Game");
        _receiver.MakeSave(saveName: "My Game", seed: 88);
        _receiver.MakeSave(saveName: "Occupied", seed: 99);
        var occupied = Manifest.Build(_receiver.Slot(saveName: "Occupied").Folder);

        var package = _sender.NewEngine().Export(_sender.Slot(saveName: "My Game"), _outbox).PackageDir;

        var peer = StartReceiver();
        var client = new LanClient(_sender.Config);
        await client.SendPackageAsync(peer, package, "Ryan");

        var item = Assert.Single((await client.InboxAsync(peer, "Ryan"))!);
        var result = await client.KeepBothAsync(peer, item.Id, "Occupied", "Ryan");

        Assert.False(result.Ok);
        Assert.Empty(occupied.Verify(_receiver.Slot(saveName: "Occupied").Folder));
    }

    [Fact]
    public async Task Asking_about_a_save_that_is_not_waiting_is_refused()
    {
        var peer = StartReceiver();
        var result = await new LanClient(_sender.Config)
            .KeepBothAsync(peer, "no-such-thing", "Whatever", "Ryan");

        Assert.False(result.Ok);
    }
}
