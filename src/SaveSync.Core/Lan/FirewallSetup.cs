using System.Diagnostics;
using System.Security.Principal;

namespace SaveSync.Core.Lan;

/// <summary>
/// The one-time Windows permission that lets the two PCs talk.
///
/// Windows blocks a program from receiving anything over the network until a rule exists, and
/// creating that rule needs administrator rights once. Doing it here, up front, replaces the
/// alarming "Windows Defender Firewall has blocked some features of this app" popup with a single
/// ordinary Yes, and then it never comes up again on that PC.
///
/// The rules are deliberately keyed to the port rather than to the program's path: this program is
/// meant to live on a USB stick, and a stick is not always the same drive letter.
/// </summary>
public static class FirewallSetup
{
    public const string TcpRuleName = "7 Days to Die Save Transfer (TCP)";
    public const string UdpRuleName = "7 Days to Die Save Transfer (UDP)";

    /// <summary>How many ports above the preferred one the listener may fall back to.</summary>
    public const int PortSearchRange = 8;

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>True when both rules already exist, so nothing needs asking.</summary>
    public static bool IsConfigured()
        => RuleExists(TcpRuleName) && RuleExists(UdpRuleName);

    private static bool RuleExists(string name)
    {
        var (code, output) = Netsh($"advfirewall firewall show rule name=\"{name}\"");
        return code == 0 && output.Contains("Rule Name", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates the rules. Only works when already running as administrator; use
    /// <see cref="RequestElevatedSetup"/> to get there.
    /// </summary>
    public static bool Configure(int tcpPort, int udpPort)
    {
        if (!IsElevated()) return false;

        Netsh($"advfirewall firewall delete rule name=\"{TcpRuleName}\"");
        Netsh($"advfirewall firewall delete rule name=\"{UdpRuleName}\"");

        // A range, not a single port: if the preferred port is busy the listener steps up to the
        // next one, and a rule covering only the first would leave it silently unreachable.
        var tcpRange = $"{tcpPort}-{tcpPort + PortSearchRange}";

        // profile=any rather than private only: Windows quietly labels plenty of home networks as
        // Public, and a rule that does not apply is indistinguishable from a broken program.
        var (tcp, _) = Netsh($"advfirewall firewall add rule name=\"{TcpRuleName}\" "
                             + $"dir=in action=allow protocol=TCP localport={tcpRange} profile=any enable=yes");
        var (udp, _) = Netsh($"advfirewall firewall add rule name=\"{UdpRuleName}\" "
                             + $"dir=in action=allow protocol=UDP localport={udpPort} profile=any enable=yes");

        return tcp == 0 && udp == 0;
    }

    public static void Remove()
    {
        Netsh($"advfirewall firewall delete rule name=\"{TcpRuleName}\"");
        Netsh($"advfirewall firewall delete rule name=\"{UdpRuleName}\"");
    }

    /// <summary>
    /// Relaunches this program with administrator rights just long enough to create the rules.
    /// Returns false if the user dismissed the Windows prompt.
    /// </summary>
    public static bool RequestElevatedSetup(int tcpPort, int udpPort)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--setup-network {tcpPort} {udpPort}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            process.WaitForExit(30_000);
            return IsConfigured();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user said no to the Windows prompt.
            return false;
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static (int Code, string Output) Netsh(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null) return (-1, "");

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);
            return (process.ExitCode, output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "");
        }
    }
}
