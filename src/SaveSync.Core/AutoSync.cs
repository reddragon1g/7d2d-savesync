namespace SaveSync.Core;

/// <summary>Why a full check is being run, so the UI and the log can say what prompted it.</summary>
public enum AutoSyncTrigger
{
    /// <summary>Nothing to do right now.</summary>
    None,

    /// <summary>Nothing has been checked yet this session.</summary>
    FirstLook,

    /// <summary>The other PC has just come online - somebody came home.</summary>
    PeerArrived,

    /// <summary>The game has just been closed, so whatever was played is now worth sending.</summary>
    GameClosed,

    /// <summary>Nothing happened; this is the slow safety net that guarantees things converge.</summary>
    Heartbeat,
}

/// <summary>What has been seen so far. Kept by the caller and handed back each time.</summary>
public sealed class AutoSyncState
{
    public DateTimeOffset? LastFullCheck { get; set; }
    public bool PeerWasPresent { get; set; }
    public bool GameWasRunning { get; set; }

    /// <summary>Set when a check actually moved something, for the "last updated" line.</summary>
    public DateTimeOffset? LastTransfer { get; set; }

    public string LastResult { get; set; } = "";
}

/// <summary>
/// When to do a full comparison with the other PC.
///
/// The frequent part of watching another machine has to stay a presence ping - "are you there" -
/// because the full comparison asks the far side to describe every save it has, which means
/// walking every file of every save. Doing that every fifteen seconds on a 600 MB world is a
/// background job nobody asked for, on both machines at once.
///
/// So the full comparison is driven by the two moments it actually matters - the other PC
/// appearing, and the game closing - with a slow heartbeat underneath that guarantees the two
/// machines converge even if both of those are missed. Every rule here is a pure decision from
/// observed facts, so the behaviour can be tested without a network or a game.
/// </summary>
public static class AutoSyncPolicy
{
    /// <summary>The safety net. Long on purpose: the event triggers are what make it feel instant.</summary>
    public static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromHours(2);

    /// <summary>
    /// Works out whether to run a full check now, and records what was seen for next time.
    ///
    /// <paramref name="gameRunning"/> beats everything. A save being written by the game is the one
    /// thing that must never be copied, and while somebody is playing there is nothing useful to
    /// do anyway - so during a session this collapses to asking whether the game is still running.
    /// </summary>
    public static AutoSyncTrigger Decide(
        bool enabled,
        bool gameRunning,
        bool peerPresent,
        AutoSyncState state,
        DateTimeOffset now,
        TimeSpan? heartbeat = null)
    {
        bool gameWasRunning = state.GameWasRunning;
        bool peerWasPresent = state.PeerWasPresent;

        // Record first, so an early return still leaves the next call with the truth.
        state.GameWasRunning = gameRunning;
        state.PeerWasPresent = peerPresent;

        if (!enabled) return AutoSyncTrigger.None;

        // Never while the game has the save open.
        if (gameRunning) return AutoSyncTrigger.None;

        // Nobody to talk to. Not a failure - the other PC is simply off.
        if (!peerPresent) return AutoSyncTrigger.None;

        // Just finished playing: whatever was played is now worth sending, and this is the moment
        // the answer is freshest.
        if (gameWasRunning) return AutoSyncTrigger.GameClosed;

        // They just came home.
        if (!peerWasPresent) return AutoSyncTrigger.PeerArrived;

        if (state.LastFullCheck is null) return AutoSyncTrigger.FirstLook;

        return now - state.LastFullCheck.Value >= (heartbeat ?? DefaultHeartbeat)
            ? AutoSyncTrigger.Heartbeat
            : AutoSyncTrigger.None;
    }

    public static string Explain(AutoSyncTrigger trigger) => trigger switch
    {
        AutoSyncTrigger.FirstLook => "first check since starting up",
        AutoSyncTrigger.PeerArrived => "the other PC came online",
        AutoSyncTrigger.GameClosed => "the game was closed",
        AutoSyncTrigger.Heartbeat => "routine check",
        _ => "",
    };
}
