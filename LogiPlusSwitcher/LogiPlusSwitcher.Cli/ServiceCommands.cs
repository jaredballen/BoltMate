using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LogiPlusSwitcher.Cli;

/// <summary>
/// Per-platform install/uninstall/status for the headless monitor service.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>macOS</b>: launchd LaunchAgent at <c>~/Library/LaunchAgents/com.jaredballen.logiplusswitcher.plist</c>, runs at user login, restarts on crash. Logs to <c>~/Library/Logs/LogiPlusSwitcher/</c>.</description></item>
/// <item><description><b>Windows</b>: scheduled task <c>LogiPlusSwitcher</c> via schtasks, runs at user logon under the current user. Chose Task Scheduler over a Windows Service because HID access needs user session context.</description></item>
/// </list>
/// </remarks>
internal static class ServiceCommands
{
    private const string LaunchAgentLabel = "com.jaredballen.logiplusswitcher";
    private const string WindowsTaskName = "LogiPlusSwitcher";

    public static int Install()
    {
        var binaryPath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not determine running binary path.");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return InstallMac(binaryPath);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return InstallWindows(binaryPath);

        Console.Error.WriteLine("Service install is supported on macOS and Windows only.");
        return 2;
    }

    public static int Uninstall()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return UninstallMac();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return UninstallWindows();

        Console.Error.WriteLine("Service uninstall is supported on macOS and Windows only.");
        return 2;
    }

    public static int Status()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return StatusMac();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return StatusWindows();

        Console.Error.WriteLine("Service status is supported on macOS and Windows only.");
        return 2;
    }

    // ---------- macOS launchd ----------

    private static string MacPlistPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "LaunchAgents", $"{LaunchAgentLabel}.plist");
    }

    private static int InstallMac(string binaryPath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var logsDir = Path.Combine(home, "Library", "Logs", "LogiPlusSwitcher");
        Directory.CreateDirectory(logsDir);

        var plistPath = MacPlistPath();
        Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);

        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTD/PropertyList-1.0.dtd">
            <plist version="1.0">
              <dict>
                <key>Label</key>
                <string>{LaunchAgentLabel}</string>
                <key>ProgramArguments</key>
                <array>
                  <string>{binaryPath}</string>
                  <string>monitor</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                  <key>SuccessfulExit</key>
                  <false/>
                </dict>
                <key>ThrottleInterval</key>
                <integer>10</integer>
                <key>StandardOutPath</key>
                <string>{logsDir}/stdout.log</string>
                <key>StandardErrorPath</key>
                <string>{logsDir}/stderr.log</string>
              </dict>
            </plist>
            """;

        File.WriteAllText(plistPath, plist, Encoding.UTF8);
        Console.WriteLine($"Wrote LaunchAgent plist: {plistPath}");

        // Unload first in case it was already loaded with stale config.
        Run("launchctl", $"unload \"{plistPath}\"", ignoreFailure: true);
        var (code, output) = Run("launchctl", $"load -w \"{plistPath}\"");
        Console.WriteLine(output);
        if (code != 0)
        {
            Console.Error.WriteLine("launchctl load failed.");
            return code;
        }

        Console.WriteLine();
        Console.WriteLine($"Installed. Logs: {logsDir}");
        Console.WriteLine("macOS may prompt for Input Monitoring permission the first time the agent runs.");
        Console.WriteLine("System Settings → Privacy & Security → Input Monitoring → enable for this binary.");
        return 0;
    }

    private static int UninstallMac()
    {
        var plistPath = MacPlistPath();
        if (!File.Exists(plistPath))
        {
            Console.WriteLine("No LaunchAgent installed.");
            return 0;
        }

        Run("launchctl", $"unload \"{plistPath}\"", ignoreFailure: true);
        File.Delete(plistPath);
        Console.WriteLine($"Removed LaunchAgent: {plistPath}");
        return 0;
    }

    private static int StatusMac()
    {
        var plistPath = MacPlistPath();
        Console.WriteLine($"Plist:      {plistPath}");
        Console.WriteLine($"  exists:   {File.Exists(plistPath)}");

        var (_, output) = Run("launchctl", $"list {LaunchAgentLabel}", ignoreFailure: true);
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine("  loaded:   no");
            return 0;
        }
        Console.WriteLine("  loaded:   yes");
        Console.WriteLine();
        Console.WriteLine(output);
        return 0;
    }

    // ---------- Windows Task Scheduler ----------

    private static int InstallWindows(string binaryPath)
    {
        // schtasks /create /tn LogiPlusSwitcher /tr "<binary> monitor" /sc onlogon /rl highest /f
        var quotedBinary = $"\\\"{binaryPath}\\\"";
        var args = $"/create /tn {WindowsTaskName} /tr \"{quotedBinary} monitor\" /sc onlogon /rl limited /f";
        var (code, output) = Run("schtasks", args);
        Console.WriteLine(output);
        if (code != 0)
        {
            Console.Error.WriteLine("schtasks /create failed.");
            return code;
        }

        // Start it now too.
        Run("schtasks", $"/run /tn {WindowsTaskName}", ignoreFailure: true);

        Console.WriteLine();
        Console.WriteLine("Installed. The task will run at next user logon and was started now.");
        Console.WriteLine("Inspect with: schtasks /query /tn LogiPlusSwitcher /v /fo list");
        return 0;
    }

    private static int UninstallWindows()
    {
        var (code, output) = Run("schtasks", $"/delete /tn {WindowsTaskName} /f", ignoreFailure: true);
        Console.WriteLine(output);
        return code;
    }

    private static int StatusWindows()
    {
        var (_, output) = Run("schtasks", $"/query /tn {WindowsTaskName} /v /fo list", ignoreFailure: true);
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine($"Scheduled task '{WindowsTaskName}' is not installed.");
            return 0;
        }
        Console.WriteLine(output);
        return 0;
    }

    // ---------- helpers ----------

    private static (int ExitCode, string Output) Run(string fileName, string args, bool ignoreFailure = false)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        if (p is null)
            return (-1, $"Failed to start: {fileName}");

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        var combined = (stdout + (string.IsNullOrEmpty(stderr) ? "" : "\n" + stderr)).Trim();

        if (p.ExitCode != 0 && !ignoreFailure)
            Console.Error.WriteLine($"  {fileName} exited with {p.ExitCode}");

        return (p.ExitCode, combined);
    }
}
