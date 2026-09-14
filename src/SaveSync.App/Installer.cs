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
    public static void Install()
    {
        var source = Environment.ProcessPath
                     ?? throw new InvalidOperationException("Cannot work out where this program is running from.");

        Directory.CreateDirectory(InstallDir);

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
        CreateStartMenuShortcut();
    }

    public static void Uninstall()
    {
        SetStartWithWindows(false);

        try { File.Delete(StartMenuShortcut); } catch (IOException) { }
        catch (UnauthorizedAccessException) { }

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

    private static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "7 Days to Die Save Transfer.lnk");

    /// <summary>
    /// Creates the Start Menu entry via a one-line script, so there is no COM interop dependency
    /// to go wrong inside a single-file build.
    /// </summary>
    private static void CreateStartMenuShortcut()
    {
        try
        {
            var script = Path.Combine(Path.GetTempPath(), "savesync-shortcut.ps1");
            File.WriteAllText(script,
                "$s = (New-Object -ComObject WScript.Shell).CreateShortcut('" + StartMenuShortcut + "')\n" +
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
            // A missing Start Menu entry is cosmetic.
        }
    }

    /// <summary>Removes the previous executable left behind by an update.</summary>
    public static void CleanupAfterUpdate()
    {
        var stale = InstalledExe + ".old";
        try { if (File.Exists(stale)) File.Delete(stale); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

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
