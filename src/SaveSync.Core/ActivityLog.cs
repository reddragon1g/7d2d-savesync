using System.Text;

namespace SaveSync.Core;

/// <summary>
/// A plain-text account of what this copy actually did.
///
/// Two audiences. Somebody who arrives somewhere and finds an old save needs to be able to see
/// whether the program ever looked, what it found, and why it decided to do nothing - "it never
/// updated" is a symptom, and without this there is no way to get from it to a cause. And a PC
/// nobody can reach needs to be able to explain itself, which is why the log is copied onto the
/// USB stick whenever one is around: the stick comes home and brings the other machine's account
/// of itself with it.
///
/// Nothing here may ever throw. A logger that can break the program it is reporting on is worse
/// than no logger at all, so every failure is swallowed and logging simply stops.
/// </summary>
public static class ActivityLog
{
    public const string FileName = "savesync.log";

    /// <summary>Rotated at this size, keeping one previous file. A stick is not a hard drive.</summary>
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string? _path;
    private static string _label = "";

    /// <summary>Where it is writing, or null when nothing has opened it. Tests never open it.</summary>
    public static string? FilePath
    {
        get { lock (Gate) return _path; }
    }

    /// <summary>
    /// Starts logging into the workspace.
    ///
    /// Until this is called every Write is a no-op - which is what keeps a test run from writing
    /// into somebody's real folders, a mistake this project has already made once with settings.
    /// </summary>
    public static void Open(Workspace workspace, string machineLabel)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(workspace.Logs);
                _path = Path.Combine(workspace.Logs, FileName);
                _label = machineLabel;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _path = null;
            }
        }
    }

    public static void Close()
    {
        lock (Gate) { _path = null; _label = ""; }
    }

    /// <summary>One line. Keep it readable by somebody who did not write the program.</summary>
    public static void Write(string what)
    {
        lock (Gate)
        {
            if (_path is null) return;

            try
            {
                RotateIfBig();
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {_label,-16}  {what}";

                // No byte-order mark. Encoding.UTF8 writes one on the first append, which shows up
                // as a stray character at the top of a file whose whole purpose is being read.
                File.AppendAllText(_path, line + Environment.NewLine, NoBom);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never be the thing that breaks.
            }
        }
    }

    public static void Write(string what, Exception error) => Write($"{what}: {error.GetType().Name}: {error.Message}");

    /// <summary>A blank line and a heading, so a session is easy to find by eye.</summary>
    public static void Session(string what)
    {
        Write("");
        Write("======== " + what + " ========");
    }

    private static void RotateIfBig()
    {
        if (_path is null) return;

        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length < MaxBytes) return;

            var previous = _path + ".1";
            try { File.Delete(previous); } catch (IOException) { }
            File.Move(_path, previous);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Copies this machine's log onto the stick, under its own name.
    ///
    /// This is the only way an account of what happened on somebody else's PC ever comes back:
    /// the stick is plugged in there, the log is written beside the saves, and the next person to
    /// plug that stick in anywhere can read it.
    /// </summary>
    public static void MirrorTo(string stickRoot)
    {
        string? source;
        string label;
        lock (Gate) { source = _path; label = _label; }

        if (source is null || !File.Exists(source)) return;

        try
        {
            var dir = Path.Combine(stickRoot, PackageLayout.RootFolderName, "logs");
            Directory.CreateDirectory(dir);

            var name = PathUtil.Sanitize(string.IsNullOrWhiteSpace(label) ? "unknown" : label) + ".log";
            File.Copy(source, Path.Combine(dir, name), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Every machine's log found on a stick, newest first. Read side of MirrorTo.</summary>
    public static List<string> OnStick(string stickRoot)
    {
        try
        {
            var dir = Path.Combine(stickRoot, PackageLayout.RootFolderName, "logs");
            if (!Directory.Exists(dir)) return new List<string>();

            return Directory.GetFiles(dir, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }
}
