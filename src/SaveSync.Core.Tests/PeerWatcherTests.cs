using System.Net;
using SaveSync.Core;
using SaveSync.Core.Lan;
using Xunit;

namespace SaveSync.Core.Tests;

/// <summary>
/// The trip-home scenario, end to end over a real socket.
///
/// He plays on the laptop while away. Both machines come back onto the same network. The desktop
/// works out on its own that the laptop is holding something newer, asks for it, and the laptop
/// packages and sends it - with the desktop's own engine still deciding whether to apply.
/// </summary>
public class PeerWatcherTests : IDisposable
{
    private readonly TestEnv _desktop = new();
    private readonly TestEnv _laptop = new();

    private LanServer? _desktopServer;
    private LanServer? _laptopServer;

    public PeerWatcherTests()
    {
        _desktop.Config.MachineId = "desktop00001";
        _desktop.Config.DisplayName = "DESKTOP";
        _laptop.Config.MachineId = "laptop000001";
        _laptop.Config.DisplayName = "LAPTOP";
    }

    public void Dispose()
    {
        _desktopServer?.Dispose();
        _laptopServer?.Dispose();
        _desktop.Dispose();
        _laptop.Dispose();
    }

    private (TransferEngine Engine, LanServer Server, LanPeer AsPeer) Bring(TestEnv env, string person)
    {
        var engine = env.NewEngine();
        engine.Identity = person;

        var server = new LanServer(env.Config, () => engine) { BindAddress = IPAddress.Loopback };
        server.Start();
        Assert.True(server.Running, $"listener did not start: {server.StartFailure}");

        var peer = new LanPeer
        {
            MachineId = env.Config.MachineId,
            DisplayName = env.Config.DisplayName,
            Address = "127.0.0.1",
            Port = server.Port,
            PersonName = person,
            LastSeen = DateTimeOffset.UtcNow,
        };

        return (engine, server, peer);
    }

    /// <summary>
    /// Turns "it did not arrive" into a statement of which half went wrong.
    ///
    /// The two explanations need completely different fixes and look identical from the assertion:
    /// a package sitting in the inbox means the receiving engine decided NOT to auto-apply, which
    /// is a real defect; an empty inbox means it never got there in time, which is a slow machine.
    /// Without this the failure is a coin toss with no evidence either way.
    /// </summary>
    private static string WhyNotArrived(TransferEngine engine)
    {
        var waiting = Inbox.List(engine.Workspace);
        if (waiting.Count == 0)
            return "The laptop's save never reached the desktop - nothing is waiting in the inbox either. "
                   + "The transfer did not complete in time (a slow or loaded machine), rather than being refused.";

        var lines = waiting.Select(w =>
        {
            var plan = engine.Inspect(w.Dir);
            return $"  {w.Display}: relation={plan.Relation} oneClickSafe={plan.IsOneClickSafe} "
                   + $"needsChoice={plan.NeedsHumanChoice} playedSinceLastCopy={plan.LocalPlayedSinceLastCopy}";
        });

        return "The package ARRIVED but was not applied automatically, which is a real defect - "
               + "the desktop had not been played since its last copy, so this should have been a "
               + "clean fast-forward:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    [Fact]
    public async Task Desktop_notices_the_laptop_has_a_newer_save_and_fetches_it()
    {
        // Both start from the same copy.
        _desktop.MakeSave();
        var outbox = Path.Combine(_desktop.Root, "outbox");
        Directory.CreateDirectory(outbox);

        var desktop = Bring(_desktop, "Ryan");
        var laptop = Bring(_laptop, "Ryan");
        _desktopServer = desktop.Server;
        _laptopServer = laptop.Server;

        var seed = desktop.Engine.Export(_desktop.Slot(), outbox).PackageDir;
        Assert.True((await new LanClient(_desktop.Config).SendPackageAsync(laptop.AsPeer, seed, "Ryan")).Sent);

        // Away from the house: the laptop is played and never copied anywhere.
        _laptop.Play(_laptop.Slot().Folder, seed: 51);
        var laptopEvening = Manifest.Build(_laptop.Slot().Folder);

        // Home again. The desktop asks what the laptop has.
        var watcher = new PeerWatcher(_desktop.Config, () => new[] { laptop.AsPeer }, () => desktop.Engine)
        {
            PersonName = "Ryan",
            ReplyPort = desktop.Server.Port,
        };

        await watcher.PollAsync();

        var news = Assert.Single(watcher.News);
        Assert.Equal(SyncDirection.ToPc, news.Direction);
        Assert.True(news.WorthFetching);
        Assert.Contains("played", news.Summary, StringComparison.OrdinalIgnoreCase);

        // One ask: the laptop packages it and sends it back on its own.
        Assert.True(await watcher.FetchAsync(news));

        // 60s, not 30: this waits on a real socket transfer plus a full verify, and the whole
        // suite's own run time varies by a factor of two on a loaded machine. The failure message
        // matters more than the number - see WhyNotArrived.
        bool arrived = await WaitUntilAsync(
            () => laptopEvening.Verify(_desktop.Slot().Folder).Count == 0,
            TimeSpan.FromSeconds(60));

        Assert.True(arrived, WhyNotArrived(desktop.Engine));

        // What arrived is the laptop's session, byte for byte, players included.
        Assert.Empty(laptopEvening.Verify(_desktop.Slot().Folder));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_desktop.Slot().Folder, "Player"), "*.ttp").Length);
    }

    [Fact]
    public async Task Nothing_to_do_when_both_PCs_already_match()
    {
        _desktop.MakeSave();
        var outbox = Path.Combine(_desktop.Root, "outbox");
        Directory.CreateDirectory(outbox);

        var desktop = Bring(_desktop, "Ryan");
        var laptop = Bring(_laptop, "Ryan");
        _desktopServer = desktop.Server;
        _laptopServer = laptop.Server;

        var seed = desktop.Engine.Export(_desktop.Slot(), outbox).PackageDir;
        Assert.True((await new LanClient(_desktop.Config).SendPackageAsync(laptop.AsPeer, seed, "Ryan")).Sent);

        var watcher = new PeerWatcher(_desktop.Config, () => new[] { laptop.AsPeer }, () => desktop.Engine)
        {
            PersonName = "Ryan",
            ReplyPort = desktop.Server.Port,
        };

        await watcher.PollAsync();

        var news = Assert.Single(watcher.News);
        Assert.Equal(SyncDirection.UpToDate, news.Direction);
        Assert.False(news.WorthFetching);
    }

    /// <summary>
    /// Both machines played since they last matched. The desktop must report a conflict rather
    /// than quietly hauling the laptop's copy over the top of an evening it has not saved.
    /// </summary>
    [Fact]
    public async Task Both_played_is_reported_as_a_conflict_and_nothing_moves()
    {
        _desktop.MakeSave();
        var outbox = Path.Combine(_desktop.Root, "outbox");
        Directory.CreateDirectory(outbox);

        var desktop = Bring(_desktop, "Ryan");
        var laptop = Bring(_laptop, "Sarah");
        _desktopServer = desktop.Server;
        _laptopServer = laptop.Server;

        var seed = desktop.Engine.Export(_desktop.Slot(), outbox).PackageDir;
        Assert.True((await new LanClient(_desktop.Config).SendPackageAsync(laptop.AsPeer, seed, "Ryan")).Sent);

        _laptop.Play(_laptop.Slot().Folder, seed: 8);
        _desktop.Play(_desktop.Slot().Folder, seed: 9);
        var desktopEvening = Manifest.Build(_desktop.Slot().Folder);

        var watcher = new PeerWatcher(_desktop.Config, () => new[] { laptop.AsPeer }, () => desktop.Engine)
        {
            PersonName = "Ryan",
            ReplyPort = desktop.Server.Port,
        };

        await watcher.PollAsync();

        var news = Assert.Single(watcher.News);
        Assert.Equal(SyncDirection.Conflict, news.Direction);
        Assert.False(news.WorthFetching);

        // Even if it is fetched anyway, the desktop refuses to apply it over unsaved play.
        Assert.True(await watcher.FetchAsync(news));
        await Task.Delay(2000);

        Assert.Empty(desktopEvening.Verify(_desktop.Slot().Folder));
    }

    [Fact]
    public async Task A_save_the_other_PC_does_not_have_is_offered()
    {
        _laptop.MakeSave(saveName: "Her Game", seed: 21);

        var desktop = Bring(_desktop, "Ryan");
        var laptop = Bring(_laptop, "Sarah");
        _desktopServer = desktop.Server;
        _laptopServer = laptop.Server;

        // The laptop has to have copied it at least once for there to be anything to describe.
        var outbox = Path.Combine(_laptop.Root, "outbox");
        Directory.CreateDirectory(outbox);
        laptop.Engine.Export(_laptop.Slot(saveName: "Her Game"), outbox);

        var watcher = new PeerWatcher(_desktop.Config, () => new[] { laptop.AsPeer }, () => desktop.Engine)
        {
            PersonName = "Ryan",
            ReplyPort = desktop.Server.Port,
        };

        await watcher.PollAsync();

        var news = Assert.Single(watcher.News);
        Assert.Equal(SyncDirection.ToPc, news.Direction);
        Assert.Equal(Relation.NoLocal, news.Relation);

        Assert.True(await watcher.FetchAsync(news));
        Assert.True(await WaitUntilAsync(
            () => SaveDiscovery.Find(_desktop.Location, "Navezgane", "Her Game") is not null,
            TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// First setup over the network: this PC has saves, the other has nothing. Walking only the
    /// far side's list found nothing to do, which left the send button empty at exactly the moment
    /// everything needed sending.
    /// </summary>
    [Fact]
    public async Task Saves_the_other_PC_does_not_have_are_offered_for_sending()
    {
        _desktop.MakeSave(saveName: "His Game", seed: 1);
        _desktop.MakeSave(saveName: "Her Game", seed: 2);

        var desktop = Bring(_desktop, "Ryan");
        var laptop = Bring(_laptop, "Sarah");
        _desktopServer = desktop.Server;
        _laptopServer = laptop.Server;

        var watcher = new PeerWatcher(_desktop.Config, () => new[] { laptop.AsPeer }, () => desktop.Engine)
        {
            PersonName = "Ryan",
            ReplyPort = desktop.Server.Port,
        };

        await watcher.PollAsync();

        Assert.Equal(2, watcher.News.Count);
        Assert.All(watcher.News, n => Assert.Equal(SyncDirection.ToStick, n.Direction));
        Assert.All(watcher.News, n => Assert.NotNull(n.Local));
        Assert.Contains(watcher.News, n => n.Remote.SaveName == "His Game");
        Assert.Contains(watcher.News, n => n.Remote.SaveName == "Her Game");
    }

    [Fact]
    public async Task A_save_on_both_sides_is_not_also_offered_for_sending()
    {
        _desktop.MakeSave();
        var outbox = Path.Combine(_desktop.Root, "outbox");
        Directory.CreateDirectory(outbox);

        var desktop = Bring(_desktop, "Ryan");
        var laptop = Bring(_laptop, "Sarah");
        _desktopServer = desktop.Server;
        _laptopServer = laptop.Server;

        var seed = desktop.Engine.Export(_desktop.Slot(), outbox).PackageDir;
        Assert.True((await new LanClient(_desktop.Config).SendPackageAsync(laptop.AsPeer, seed, "Ryan")).Sent);

        var watcher = new PeerWatcher(_desktop.Config, () => new[] { laptop.AsPeer }, () => desktop.Engine)
        {
            PersonName = "Ryan",
            ReplyPort = desktop.Server.Port,
        };

        await watcher.PollAsync();

        // Exactly one entry, and it is the matched pair - not a duplicate "they do not have it".
        var news = Assert.Single(watcher.News);
        Assert.Equal(SyncDirection.UpToDate, news.Direction);
    }

    [Fact]
    public async Task No_peers_means_no_news()
    {
        _desktop.MakeSave();
        var desktop = Bring(_desktop, "Ryan");
        _desktopServer = desktop.Server;

        var watcher = new PeerWatcher(_desktop.Config, Array.Empty<LanPeer>, () => desktop.Engine);
        await watcher.PollAsync();

        Assert.Empty(watcher.News);
    }
}
