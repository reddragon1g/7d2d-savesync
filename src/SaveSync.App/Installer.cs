using System.Diagnostics;
using Microsoft.Win32;
using SaveSync.Core;

namespace SaveSync.App;

/// <summary>
/// Installs the program onto a PC so it can run quietly in the background.
///
/// The portable copy on the USB stick only exists while somebody has it open, so it can never
/// notice that the other PC came home with a newer save. Installing puts a copy in the user's own
/// folder and starts it at login, which is what makes the automatic side work.
///
/// Deliberately not a Windows service: a service runs outside the user's session, so it cannot
/// reliably reach the user's save folder and cannot show them anything.
/// </summary>
public static class Installer
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "7DaysToDieSaveTransfer";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaveSync", "app");

    public static string InstalledExe => Path.Combine(InstallDir, "SaveSync.exe");

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>The version of this running program.</summary>
    public static Version ThisVersion =>
        typeof(Installer).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>The version already installed on this PC, or null when nothing is installed.</summary>
    public static Version? InstalledVersion
    {
        get
        {
            if (!IsInstalled) return null;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(InstalledExe);
                return Version.TryParse(info.FileVersion, out var v) ? v : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>True when the copy installed here is older than the one running now.</summary>
    public static bool InstalledIsOlder => InstalledVersion is { } v && v < ThisVersion;

    /// <summary>
    /// Closes the installed copy so it can be replaced.
    ///
    /// An installed copy starts with Windows and sits in the tray, so during an update it is
    /// always running and always holding the single-instance slot. Asking politely first means it
    /// shuts down its listener and saves its settings; killing is the fallback, and the worst it
    /// can cost is a half-written settings file, which is written atomically anyway.
    /// </summary>
    public static bool StopInstalledCopy(TimeSpan timeout)
    {
        bool stoppedAny = false;
        var deadline = DateTime.UtcNow + timeout;

        foreach (var p in SafeProcesses())
        {
            try
            {
                if (!PathUtil.SamePath(p.MainModule?.FileName ?? "", InstalledExe)) continue;
                if (p.Id == Environment.ProcessId) continue;

                stoppedAny = true;
                p.CloseMainWindow();

                while (DateTime.UtcNow < deadline && !p.HasExited) Thread.Sleep(100);
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                p.WaitForExit((int)Math.Max(1000, (deadline - DateTime.UtcNow).TotalMilliseconds));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception
                                      or NotSupportedException or IOException)
            {
                // A process that vanished, or one this user may not touch. Either way, move on.
            }
            finally { p.Dispose(); }
        }

        return stoppedAny;
    }

    private static Process[] SafeProcesses()
    {
        try { return Process.GetProcessesByName("SaveSync"); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Array.Empty<Process>();
        }
    }

    /// <summary>True when this process is the installed copy rather than the one on the stick.</summary>
    public static bool RunningInstalled =>
        PathUtil.SamePath(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", InstallDir);

    public static bool StartsWithWindows
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) is string s && s.Contains("SaveSync", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Copies this program into the user's own folder and sets it to start with Windows.
    /// Needs no administrator rights: everything it touches belongs to the current user.
    /// </summary>
    public static void Install() => InstallFrom(
        Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot work out where this program is running from."));

    /// <summary>
    /// Puts a particular program file in place as the installed copy.
    ///
    /// Split out from Install so a program that arrived over the network can be installed the same
    /// way as the one currently running - same stop, same move-aside, same startup entry. One path
    /// means one set of behaviour to get right.
    /// </summary>
    public static void InstallFrom(string source)
    {
        if (!File.Exists(source))
            throw new FileNotFoundException("That program file is not there.", source);

        Directory.CreateDirectory(InstallDir);

        // An installed copy is running almost by definition - it starts with Windows. It has to go
        // before its file can be replaced, and before the new one can claim the single-instance slot.
        StopInstalledCopy(TimeSpan.FromSeconds(10));

        // Copying onto a running executable fails, so the old one is moved aside first and
        // cleaned up on the next launch.
        if (File.Exists(InstalledExe) && !PathUtil.SamePath(source, InstalledExe))
        {
            var stale = InstalledExe + ".old";
            try { File.Delete(stale); } catch (IOException) { }
            try { File.Move(InstalledExe, stale, overwrite: true); } catch (IOException) { }
        }

        if (!PathUtil.SamePath(source, InstalledExe))
            File.Copy(source, InstalledExe, overwrite: true);

        SetStartWithWindows(true);
        CreateShortcuts();
    }

    public static void Uninstall()
    {
        SetStartWithWindows(false);

        foreach (var link in new[] { StartMenuShortcut, DesktopShortcut })
        {
            try { File.Delete(link); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        // The executable itself is left alone: it may be the process running this very code.
    }

    public static void SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled) key.SetValue(RunValue, $"\"{InstalledExe}\" --background");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Startup is a convenience; the program still works when opened by hand.
        }
    }

    public const string ShortcutName = "7 Days to Die Save Transfer.lnk";

    public static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName);

    public static string DesktopShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    /// <summary>True when there is some visible way to start the installed copy.</summary>
    public static bool HasShortcut => File.Exists(StartMenuShortcut) || File.Exists(DesktopShortcut);

    /// <summary>
    /// Puts the program somewhere a person can actually find it - the Start Menu AND the Desktop.
    ///
    /// Start Menu alone was not enough in practice: on two real machines no entry appeared, the
    /// failure was swallowed as "cosmetic", and the result was an installed program with no visible
    /// way to start it at all. A shortcut is not cosmetic when it is the only door.
    /// </summary>
    /// <summary>Makes the shortcuts on demand, for a PC that ended up without one.</summary>
    public static void CreateShortcutsNow() => CreateShortcuts();

    private static void CreateShortcuts()
    {
        MakeShortcut(StartMenuShortcut);
        MakeShortcut(DesktopShortcut);

        ActivityLog.Write(HasShortcut
            ? $"shortcuts made (start menu: {File.Exists(StartMenuShortcut)}, desktop: {File.Exists(DesktopShortcut)})"
            : $"NO shortcut could be made - the program can still be started from {InstalledExe}");
    }

    private static void MakeShortcut(string linkPath)
    {
        try
        {
            var script = Path.Combine(Path.GetTempPath(),
                "savesync-shortcut-" + Guid.NewGuid().ToString("N")[..6] + ".ps1");
            File.WriteAllText(script,
                "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('" + linkPath + "')\n" +
                "$s.TargetPath = '" + InstalledExe + "'\n" +
                "$s.WorkingDirectory = '" + InstallDir + "'\n" +
                "$s.Description = '7 Days to Die Save Transfer'\n" +
                "$s.Save()\n");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(15_000);

            try { File.Delete(script); } catch (IOException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                  or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ActivityLog.Write($"could not create the shortcut at {linkPath}", e);
        }
    }

    /// <summary>Removes the previous executable left behind by an update.</summary>
    public static void CleanupAfterUpdate()
    {
        var stale = InstalledExe + ".old";
        try { if (File.Exists(stale)) File.Delete(stale); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Starts the installed copy for a handover, telling it to be patient about the slot.
    ///
    /// The copy doing the handing over is still running and still holding the single-instance
    /// slot; the new one has to outlast that shutdown rather than give up on it.
    /// </summary>
    public static bool LaunchInstalledForHandover(bool background)
        => LaunchInstalled(background ? "--handover --background" : "--handover");

    /// <summary>Starts the installed copy and returns true if it took over.</summary>
    public static bool LaunchInstalled(string? arguments = null)
    {
        if (!IsInstalled) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = InstalledExe,
                Arguments = arguments ?? "",
                UseShellExecute = true,
                WorkingDirectory = InstallDir,
            });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
