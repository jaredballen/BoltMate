using System.Diagnostics;
using System.Runtime.InteropServices;
using BoltMate.Core.Autostart;

namespace BoltMate.Cli;

/// <summary>
/// Thin wrapper around <see cref="AutostartManager"/> that installs the CLI's
/// headless `monitor` mode at user login. The App project uses the same
/// manager with a distinct label so the two can be inspected/uninstalled
/// independently.
/// </summary>
internal static class ServiceCommands
{
    private const string LaunchAgentLabel = "com.jaredballen.boltmate";
    private const string WindowsTaskName = "BoltMate";

    private static string LabelForOs() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsTaskName : LaunchAgentLabel;

    public static int Install()
    {
        var binaryPath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not determine running binary path.");

        var result = AutostartManager.Install(LabelForOs(), binaryPath, ["monitor"]);
        Console.WriteLine(result.Message);

        if (!result.Success) return 1;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Console.WriteLine();
            Console.WriteLine("macOS may prompt for Input Monitoring permission the first time the agent runs.");
            Console.WriteLine("System Settings → Privacy & Security → Input Monitoring → enable for this binary.");
        }
        return 0;
    }

    public static int Uninstall()
    {
        var result = AutostartManager.Uninstall(LabelForOs());
        Console.WriteLine(result.Message);
        return result.Success ? 0 : 1;
    }

    public static int Status()
    {
        var status = AutostartManager.Status(LabelForOs());
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Console.WriteLine($"Plist:    {AutostartManager.MacPlistPath(LaunchAgentLabel)}");
            Console.WriteLine($"  exists: {status.Installed}");
            Console.WriteLine($"  loaded: {status.Loaded}");
        }
        else
        {
            Console.WriteLine($"Task '{LabelForOs()}' installed: {status.Installed}");
        }
        if (!string.IsNullOrWhiteSpace(status.Detail))
        {
            Console.WriteLine();
            Console.WriteLine(status.Detail);
        }
        return 0;
    }
}
