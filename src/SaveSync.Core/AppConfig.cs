namespace SaveSync.Core;

public sealed class PeerInfo
{
    public string MachineId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string LastAddress { get; set; } = "";
    public DateTimeOffset LastSeen { get; set; }

    /// <summary>
    /// Shared secret agreed the first time these two machines were introduced. Presence of a
    /// secret is what "paired" means; there is no separate flag to get out of step with it.
    /// </summary>
    public string? Secret { get; set; }

    public bool IsPaired => !string.IsNullOrWhiteSpace(Secret);
}

public sealed class AppConfig
{
    public int Schema { get; set; } = 1;

    /// <summary>
    /// Bumped when a DEFAULT changes in a way an existing settings file would otherwise override.
    ///
    /// Separate from Schema, which is about the file's shape. This is about its values: a setting
    /// written by an older version is indistinguishable from one the user chose, and the only way
    /// to tell them apart is to record which version's defaults the file was written against.
    /// </summary>
    public const int CurrentSettingsVersion = 2;

    public int SettingsVersion { get; set; }

    /// <summary>Stable id for this machine. Survives a rename, which a display name does not.</summary>
    public string MachineId { get; set; } = Machine.NewMachineId();

    /// <summary>
    /// Which machine wrote this file, so a settings file that travels - on the stick, or copied
    /// between PCs - can be spotted and given a fresh identity.
    ///
    /// Two PCs sharing one MachineId is the worst kind of failure here: each ignores the other's
    /// announcements as its own echo, so they simply never see each other and nothing says why.
    /// </summary>
    public string CreatedOnMachine { get; set; } = Machine.Name;

    /// <summary>Shown to other people, defaults to the Windows computer name. Detected, not typed.</summary>
    public string DisplayName { get; set; } = Machine.Name;

    /// <summary>The person picked on the opening screen last time, so their button is offered first.</summary>
    public string? LastProfileId { get; set; }

    /// <summary>Set only when discovery guessed wrong and the user browsed to the folder themselves.</summary>
    public string? UserDataOverride { get; set; }

    public string? InstallOverride { get; set; }

    public List<PeerInfo> Peers { get; set; } = new();

    /// <summary>Shared secret for LAN pairing. Null until the user pairs with another machine.</summary>
    public string? PairingKey { get; set; }

    public int LanPort { get; set; } = 47365;

    /// <summary>
    /// Let the other PC replace the program on this one.
    ///
    /// Off, and it takes a deliberate act to turn on, because it is a different kind of permission
    /// from everything else here. Pairing happens automatically and without asking anybody, which
    /// is fine while the worst a peer can do is offer a save this machine's own engine then judges
    /// for itself - and is emphatically not fine if a peer can hand over a program to run. So this
    /// is the one thing that is never granted by pairing alone.
    /// </summary>
    public bool AllowRemoteUpdate { get; set; }

    /// <summary>Transfer over the network when the other PC is reachable, instead of the stick.</summary>
    public bool UseNetwork { get; set; } = true;

    public PeerInfo? FindPeer(string machineId)
        => Peers.FirstOrDefault(p => string.Equals(p.MachineId, machineId, StringComparison.OrdinalIgnoreCase));

    public PeerInfo RememberPeer(string machineId, string displayName, string? address = null)
    {
        var peer = FindPeer(machineId);
        if (peer is null)
        {
            peer = new PeerInfo { MachineId = machineId };
            Peers.Add(peer);
        }
        if (!string.IsNullOrWhiteSpace(displayName)) peer.DisplayName = displayName;
        if (!string.IsNullOrWhiteSpace(address)) peer.LastAddress = address!;
        peer.LastSeen = DateTimeOffset.UtcNow;
        return peer;
    }

    // ---- Automation. All advisory by default: the tool may warn, it may not move anything. ----

    /// <summary>Warn when the game starts and a newer copy exists elsewhere. Prevention beats resolution.</summary>
    public bool WarnBeforePlay { get; set; } = true;

    /// <summary>Offer to send after the game exits. An offer, not an action.</summary>
    public bool PromptAfterPlay { get; set; } = true;

    /// <summary>Actually transfer without asking. Off, and stays off unless the user turns it on.</summary>
    public bool AutoSendAfterPlay { get; set; }

    /// <summary>
    /// Keep the two PCs level without anybody pressing anything.
    ///
    /// Off until asked for. It only ever moves a save when the answer is unambiguous - the same
    /// rule a person gets offered a one-click button for - so switching it on cannot make a
    /// decision that would otherwise have been put to somebody.
    /// </summary>
    public bool AutoSync { get; set; }

    /// <summary>
    /// How often to do a full comparison when nothing has happened, in minutes.
    ///
    /// Long on purpose. The full comparison asks the other PC to describe every save it holds,
    /// which walks every file of every save; doing that on a timer is a background job nobody
    /// asked for. The moments that matter - the other PC appearing, the game closing - are
    /// noticed as they happen, and this is only the net underneath them.
    /// </summary>
    public int AutoSyncEveryMinutes { get; set; } = 120;

    /// <summary>
    /// Backups retained per save. Zero - the default - keeps every one forever, so a backup only
    /// ever disappears because somebody deleted it in the Backups window. Set a number here only
    /// if disk space is genuinely short.
    /// </summary>
    public int SnapshotsToKeep { get; set; }

    /// <summary>Also carry client-side data for servers you join. Rarely wanted, so off.</summary>
    public bool IncludeSavesLocal { get; set; }

    /// <summary>
    /// Carry the mods along with the save. On, because a save played with mods does not load
    /// properly without them, so sending one without the other is the broken half of a transfer.
    ///
    /// Turning this off still records which mods were installed, so the other PC can at least say
    /// what is missing rather than silently loading a save that will misbehave.
    /// </summary>
    public bool IncludeMods { get; set; } = true;

    public DateTimeOffset? FirstRunAt { get; set; }

    /// <summary>
    /// The file this config came from, and the one it writes back to.
    ///
    /// Not a nicety. Without it every AppConfig in the process wrote to the one real user settings
    /// file - including the ones created by tests, which paired with loopback peers and then saved
    /// that over the actual user's settings. A config belongs to a file; it does not belong to
    /// whatever file happens to be the default.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SourcePath { get; set; }

    public static AppConfig Load()
    {
        var path = AppFolders.ConfigPath;
        bool existed = File.Exists(path);
        var cfg = Json.ReadFile<AppConfig>(path) ?? new AppConfig();
        cfg.SourcePath = path;

        // A file that was never on disk is brand new and already has current defaults.
        if (!existed) cfg.SettingsVersion = CurrentSettingsVersion;
        else cfg.UpgradeFromOlderVersion();

        cfg.AdoptForThisMachine(Machine.Name);
        return cfg;
    }

    /// <summary>
    /// Claims these settings for the machine now running them.
    ///
    /// A settings file can travel - copied between PCs, or sitting next to the program on the USB
    /// stick - and two machines sharing one MachineId is the worst failure available here: each
    /// dismisses the other's announcements as its own echo, so they simply never find each other
    /// and nothing anywhere says why. A borrowed identity is therefore replaced, not reused, and
    /// the paired secrets go with it since they belonged to the other machine.
    /// </summary>
    public bool AdoptForThisMachine(string machineName)
    {
        bool borrowed = !string.IsNullOrWhiteSpace(CreatedOnMachine)
                        && !string.Equals(CreatedOnMachine, machineName, StringComparison.OrdinalIgnoreCase);

        if (borrowed)
        {
            MachineId = Machine.NewMachineId();
            DisplayName = machineName;
            Peers.Clear();
        }

        if (string.IsNullOrWhiteSpace(MachineId)) MachineId = Machine.NewMachineId();
        if (string.IsNullOrWhiteSpace(DisplayName)) DisplayName = machineName;
        CreatedOnMachine = machineName;
        FirstRunAt ??= DateTimeOffset.UtcNow;

        return borrowed;
    }

    /// <summary>
    /// Writes settings wherever they can actually be written.
    ///
    /// The program is meant to run from anywhere - a stick, the Desktop, Program Files, a network
    /// share - and several of those are read-only. Falling back to the per-user folder means
    /// "cannot save settings" never becomes a reason the whole thing stops working.
    /// </summary>
    /// <summary>
    /// Brings a settings file written by an older version up to the current defaults.
    ///
    /// Runs once, and only touches values whose old default is now actively wrong. Anything the
    /// user set deliberately is left exactly as it is, because there is no way to undo a surprise
    /// like that and no reason to risk one.
    /// </summary>
    public void UpgradeFromOlderVersion()
    {
        if (SettingsVersion >= CurrentSettingsVersion) return;

        // v1 shipped a count-based backup limit of 10, which made "nothing is ever deleted" false.
        // Only the old default is cleared; a number somebody picked themselves survives.
        if (SnapshotsToKeep == 10) SnapshotsToKeep = 0;

        SettingsVersion = CurrentSettingsVersion;
    }

    public void Save()
    {
        // A config that knows where it came from writes there and nowhere else. One that does not
        // is a brand new one on a real machine, which belongs in the user's settings folder.
        if (SourcePath is { Length: > 0 } mine) { SaveTo(mine, mine); return; }
        SaveTo(AppFolders.ConfigPath, AppFolders.LocalConfigPath);
    }

    /// <summary>Split out so the fallback can be tested against a location that genuinely refuses writes.</summary>
    public void SaveTo(string preferred, string fallback)
    {
        try
        {
            Json.WriteFileAtomic(preferred, this);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (PathUtil.SamePath(preferred, fallback)) throw;
            Json.WriteFileAtomic(fallback, this);
        }
    }

    /// <summary>Resolve the game using any overrides the user has set.</summary>
    public GameLocation? Locate() => GamePaths.Discover(UserDataOverride, InstallOverride);
}
