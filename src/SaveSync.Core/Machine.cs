namespace SaveSync.Core;

/// <summary>
/// Who and where we are. Detected, never typed - the user should not have to tell the tool what
/// their PC is called, and a typo there would silently break ancestry attribution.
/// </summary>
public static class Machine
{
    public static string Name => Environment.MachineName;

    public static string User => Environment.UserName;

    /// <summary>MACHINE\user. Identifies the account that produced a version, for display and audit.</summary>
    public static string Identity => Name + "\\" + User;

    /// <summary>
    /// Stable per-machine id, kept in config. Machine names can be changed by the user; this is
    /// what peer pairing and package origin actually key on.
    /// </summary>
    public static string NewMachineId() => Guid.NewGuid().ToString("N").Substring(0, 12);
}
