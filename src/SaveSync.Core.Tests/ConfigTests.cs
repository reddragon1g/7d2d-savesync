using SaveSync.Core;
using Xunit;

namespace SaveSync.Core.Tests;

public class ConfigTests : IDisposable
{
    /// <summary>Its own temp folder - these tests are about file locations, not about saves.</summary>
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "savesync-tests", "config-" + Guid.NewGuid().ToString("N"));

    public ConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { PathUtil.DeleteTree(_dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Settings that travelled from another PC - copied across, or carried on the stick next to
    /// the program - must not clone that machine's identity. Two PCs sharing one id ignore each
    /// other's announcements as their own echo, so they never connect and nothing explains why.
    /// </summary>
    [Fact]
    public void Settings_from_another_PC_get_a_fresh_identity()
    {
        var config = new AppConfig
        {
            MachineId = "borrowed0001",
            DisplayName = "RYAN-LAPTOP",
            CreatedOnMachine = "RYAN-LAPTOP",
        };
        config.RememberPeer("someoneelse", "OTHER-PC").Secret = "a-secret-for-the-laptop";

        bool borrowed = config.AdoptForThisMachine("RYAN-DESKTOP");

        Assert.True(borrowed);
        Assert.NotEqual("borrowed0001", config.MachineId);
        Assert.Equal("RYAN-DESKTOP", config.CreatedOnMachine);
        Assert.Equal("RYAN-DESKTOP", config.DisplayName);
        Assert.Empty(config.Peers);
    }

    [Fact]
    public void Settings_from_this_PC_are_left_alone()
    {
        var config = new AppConfig
        {
            MachineId = "mine00000001",
            DisplayName = "Ryan's desktop",
            CreatedOnMachine = "RYAN-DESKTOP",
        };
        config.RememberPeer("laptop", "RYAN-LAPTOP").Secret = "keep-me";

        bool borrowed = config.AdoptForThisMachine("RYAN-DESKTOP");

        Assert.False(borrowed);
        Assert.Equal("mine00000001", config.MachineId);
        Assert.Equal("Ryan's desktop", config.DisplayName);
        Assert.Single(config.Peers);
        Assert.Equal("keep-me", config.Peers[0].Secret);
    }

    [Fact]
    public void A_brand_new_settings_file_gets_an_identity()
    {
        var config = new AppConfig { MachineId = "", CreatedOnMachine = "", DisplayName = "" };

        config.AdoptForThisMachine("SOME-PC");

        Assert.False(string.IsNullOrWhiteSpace(config.MachineId));
        Assert.Equal("SOME-PC", config.DisplayName);
        Assert.Equal("SOME-PC", config.CreatedOnMachine);
        Assert.NotNull(config.FirstRunAt);
    }

    [Fact]
    public void Machine_name_comparison_ignores_case()
    {
        var config = new AppConfig { MachineId = "keep00000001", CreatedOnMachine = "ryan-desktop" };
        Assert.False(config.AdoptForThisMachine("RYAN-DESKTOP"));
        Assert.Equal("keep00000001", config.MachineId);
    }

    // ---------------------------------------------------------------- running from anywhere

    [Fact]
    public void Settings_still_save_when_the_program_folder_refuses_writes()
    {
        // Run from a write-protected stick, from Program Files, from a network share - all real,
        // and all places where "could not save settings" must not become "the program is useless".
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "a file sitting where a folder would have to be");

        var preferred = Path.Combine(blocked, "config.json");
        var fallback = Path.Combine(_dir, "fallback", "config.json");

        new AppConfig { DisplayName = "Laptop" }.SaveTo(preferred, fallback);

        Assert.False(File.Exists(preferred));
        Assert.Equal("Laptop", Json.ReadFile<AppConfig>(fallback)!.DisplayName);
    }

    [Fact]
    public void A_settings_file_beside_the_program_is_preferred()
    {
        var beside = Path.Combine(_dir, "beside", "config.json");
        var local = Path.Combine(_dir, "local", "config.json");

        new AppConfig { DisplayName = "FromTheStick" }.SaveTo(beside, beside);

        Assert.Equal(beside, AppFolders.Choose(beside, local));
    }

    [Fact]
    public void The_newer_settings_file_wins_when_both_exist()
    {
        // This is what a read-only stick produces: the beside-EXE copy can never be updated again,
        // so preferring it regardless would mean reading stale settings forever, silently.
        var beside = Path.Combine(_dir, "beside", "config.json");
        var local = Path.Combine(_dir, "local", "config.json");

        new AppConfig { DisplayName = "Stale" }.SaveTo(beside, beside);
        Thread.Sleep(50);
        new AppConfig { DisplayName = "Current" }.SaveTo(local, local);

        Assert.Equal(local, AppFolders.Choose(beside, local));
    }

    [Fact]
    public void With_no_settings_beside_the_program_the_per_user_copy_is_used()
    {
        var beside = Path.Combine(_dir, "nothing-here", "config.json");
        var local = Path.Combine(_dir, "local", "config.json");

        Assert.Equal(local, AppFolders.Choose(beside, local));
    }

    [Fact]
    public void A_config_writes_back_to_its_own_file_and_nowhere_else()
    {
        // The regression this exists for: a LAN test paired with a loopback peer, the pairing code
        // saved the config, and AppConfig.Save wrote to the one real user settings file - so a test
        // run replaced the actual user's machine identity and peer list.
        var mine = Path.Combine(_dir, "mine", "config.json");

        var cfg = new AppConfig { SourcePath = mine, DisplayName = "TEST-ONLY" };
        cfg.RememberPeer("loopback0001", "LAPTOP", "127.0.0.1").Secret = "not-a-real-secret";
        cfg.Save();

        Assert.True(File.Exists(mine));
        Assert.Equal("TEST-ONLY", Json.ReadFile<AppConfig>(mine)!.DisplayName);

        // And nothing leaked into the place a defaulted config would have gone.
        Assert.NotEqual(PathUtil.Normalize(mine), PathUtil.Normalize(AppFolders.ConfigPath));
    }

    [Fact]
    public void An_upgrade_turns_an_old_backup_limit_into_keep_everything()
    {
        // Older versions wrote a count into the settings file. Reading that back would keep the old
        // count-based pruning alive forever on every PC that had ever run one of them - so the
        // promise on the window ("nothing is ever deleted") would stay false exactly where it had
        // already been false, and silently.
        var path = Path.Combine(_dir, "old", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"schema\": 1, \"snapshotsToKeep\": 10, \"displayName\": \"OLD-PC\" }");

        var cfg = Json.ReadFile<AppConfig>(path)!;
        cfg.SourcePath = path;
        Assert.Equal(10, cfg.SnapshotsToKeep);

        cfg.UpgradeFromOlderVersion();

        Assert.Equal(0, cfg.SnapshotsToKeep);
        Assert.Equal("OLD-PC", cfg.DisplayName);   // everything else is left alone
    }

    [Fact]
    public void Upgrading_leaves_a_limit_the_user_chose_themselves_alone()
    {
        // Only the old default is cleared. Somebody who deliberately set a small number because
        // their drive is full must not have it silently undone.
        var cfg = new AppConfig { SnapshotsToKeep = 3, SettingsVersion = AppConfig.CurrentSettingsVersion };
        cfg.UpgradeFromOlderVersion();
        Assert.Equal(3, cfg.SnapshotsToKeep);
    }
}
