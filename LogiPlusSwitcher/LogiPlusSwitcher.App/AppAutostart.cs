using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LogiPlusSwitcher.Core.Autostart;

namespace LogiPlusSwitcher.App;

/// <summary>
/// App-side "launch at login" wrapper. Picks a label distinct from the CLI
/// service so the two registrations don't collide, and resolves the
/// process-correct binary path — on macOS this is the executable inside
/// the .app bundle's Contents/MacOS; on Windows, the .exe.
/// </summary>
public static class AppAutostart
{
    private const string MacLabel = "com.jaredballen.logiplusswitcher.app";
    private const string WindowsTaskName = "LogiPlusSwitcher.App";

    public static string Label =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsTaskName : MacLabel;

    public static AutostartResult Install()
    {
        var path = ResolveProgramPath();
        if (path is null)
            return new AutostartResult(false, "Could not resolve App binary path.");
        return AutostartManager.Install(Label, path);
    }

    public static AutostartResult Uninstall() => AutostartManager.Uninstall(Label);

    public static bool IsInstalled() => AutostartManager.IsInstalled(Label);

    /// <summary>
    /// Refuse to register a `dotnet run` build — those will look for a DLL
    /// that doesn't survive the next clean. Detect by checking that we're not
    /// running under `dotnet` host with a transient publish dir.
    /// </summary>
    public static bool CanRegister()
    {
        var path = ResolveProgramPath();
        if (path is null) return false;
        var name = Path.GetFileNameWithoutExtension(path);
        // `dotnet` host means we're in `dotnet run` — autostart pointed at the
        // shared host would re-launch the host with no args, not us.
        return !string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveProgramPath()
    {
        var binary = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(binary)) return null;
        return binary;
    }
}
