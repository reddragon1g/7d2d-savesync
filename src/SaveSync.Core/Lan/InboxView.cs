namespace SaveSync.Core.Lan;

/// <summary>
/// One save waiting for a decision on a machine, described well enough to decide about it from
/// somewhere else - which save, which world, how far it got, and why it is waiting.
/// </summary>
public sealed class WaitingSave
{
    public required string Id { get; init; }
    public required string SaveName { get; init; }
    public required string World { get; init; }
    public required string FromName { get; init; }
    public string Relation { get; init; } = "";
    public string Why { get; init; } = "";

    /// <summary>In-game day, so two saves with one name can be told apart from a distance.</summary>
    public int Day { get; init; }

    public int Players { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>A name that is free on that machine right now, ready to be used as-is.</summary>
    public string SuggestedName { get; init; } = "";

    public string Display => $"{SaveName} ({World})";
}
