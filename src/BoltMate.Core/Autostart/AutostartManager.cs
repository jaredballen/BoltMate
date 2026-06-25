using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BoltMate.Core.Autostart;

/// <summary>
/// Cross-platform "launch at login" registration. macOS uses a launchd
/// LaunchAgent (~/Library/LaunchAgents/&lt;label&gt;.plist) with KeepAlive
/// restart-on-crash. Windows uses Task Scheduler with an onlogon trigger;
/// Task Scheduler over a Windows Service because HID needs user-session
/// context, not SYSTEM.
/// </summary>
public static class AutostartManager
{
    /// <param name="label">Reverse-DNS-style identifier. Used as the launchd
    /// Label and the schtasks /tn name. e.g. com.jaredballen.boltmate
    /// (CLI) vs com.jaredballen.boltmate.app (App).</param>
    /// <param name="programPath">Absolute path to the executable. On macOS .app
    /// bundles, point at the binary inside Contents/MacOS (launchd needs an
    /// actual file path, not `open -a`).</param>
    /// <param name="programArgs">Optional CLI args appended to the launch line.
    /// Empty for GUI app, "monitor" for CLI headless service.</param>
    /// <param name="logsDir">macOS-only: stdout/stderr drop path. Ignored on
    /// Windows (Task Scheduler doesn't redirect by default).</param>
    public static AutostartResult Install(
        string label,
        string programPath,
        IReadOnlyList<string>? programArgs = null,
        string? logsDir = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return InstallMac(label, programPath, programArgs ?? [], logsDir);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return InstallWindows(label, programPath, programArgs ?? []);
        return AutostartResult.Unsupported();
    }

    public static AutostartResult Uninstall(string label)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return UninstallMac(label);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return UninstallWindows(label);
        return AutostartResult.Unsupported();
    }

    public static AutostartStatus Status(string label)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return StatusMac(label);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return StatusWindows(label);
        return new AutostartStatus(false, false, "Unsupported on this platform.");
    }

    public static bool IsInstalled(string label) => Status(label).Installed;

    /// <summary>
    /// True when the autostart entry is currently active — i.e. launchd /
    /// Task Scheduler will run it at next login. Distinct from "installed":
    /// on macOS, System Settings → Login Items toggles the LOADED state
    /// without deleting the plist, so the right runtime probe is Loaded,
    /// not Installed.
    /// </summary>
    public static bool IsLoaded(string label) => Status(label).Loaded;

    /// <summary>
    /// Inverse of <see cref="Install"/>'s load step: instructs launchd /
    /// Task Scheduler to stop running this label at login, but leaves the
    /// underlying registration (plist / scheduled task) on disk. This
    /// mirrors what the macOS System Settings → Login Items toggle does
    /// when the user flips an entry off, so re-enabling stays as a single
    /// launchctl/schtasks call instead of needing a rewrite.
    /// </summary>
    public static AutostartResult Disable(string label)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return DisableMac(label);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return DisableWindows(label);
        return AutostartResult.Unsupported();
    }

    private static AutostartResult DisableMac(string label)
    {
        var plistPath = MacPlistPath(label);
        if (!File.Exists(plistPath))
            return new AutostartResult(true, "Not installed; nothing to disable.", plistPath);
        var (code, output) = Run("launchctl", $"unload \"{plistPath}\"", ignoreFailure: true);
        // launchctl unload returns non-zero when the job isn't loaded —
        // treat that as "already disabled".
        return new AutostartResult(true, code == 0 ? $"Unloaded {label}" : output, plistPath);
    }

    // ---------- macOS ----------

    public static string MacPlistPath(string label)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "LaunchAgents", $"{label}.plist");
    }

    private static AutostartResult InstallMac(
        string label,
        string programPath,
        IReadOnlyList<string> programArgs,
        string? logsDir)
    {
        logsDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Logs", "BoltMate");
        Directory.CreateDirectory(logsDir);

        var plistPath = MacPlistPath(label);
        Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);

        var argsXml = new StringBuilder();
        argsXml.Append("      <string>").Append(EscapeXml(programPath)).Append("</string>\n");
        foreach (var a in programArgs)
            argsXml.Append("      <string>").Append(EscapeXml(a)).Append("</string>\n");

        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTD/PropertyList-1.0.dtd">
            <plist version="1.0">
              <dict>
                <key>Label</key>
                <string>{EscapeXml(label)}</string>
                <key>ProgramArguments</key>
                <array>
            {argsXml.ToString().TrimEnd()}
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
                <string>{EscapeXml(logsDir)}/boltmate-startup.log</string>
                <key>StandardErrorPath</key>
                <string>{EscapeXml(logsDir)}/boltmate-startup.log</string>
              </dict>
            </plist>
            """;

        File.WriteAllText(plistPath, plist, Encoding.UTF8);

        Run("launchctl", $"unload \"{plistPath}\"", ignoreFailure: true);
        var (code, output) = Run("launchctl", $"load -w \"{plistPath}\"");
        if (code != 0)
            return new AutostartResult(false, $"launchctl load failed: {output}", plistPath);

        return new AutostartResult(true, $"LaunchAgent installed at {plistPath}", plistPath);
    }

    private static AutostartResult UninstallMac(string label)
    {
        var plistPath = MacPlistPath(label);
        if (!File.Exists(plistPath))
            return new AutostartResult(true, "Not installed; nothing to do.", plistPath);

        Run("launchctl", $"unload \"{plistPath}\"", ignoreFailure: true);
        try { File.Delete(plistPath); }
        catch (Exception ex)
        {
            return new AutostartResult(false, $"Failed to delete plist: {ex.Message}", plistPath);
        }
        return new AutostartResult(true, $"Removed {plistPath}", plistPath);
    }

    private static AutostartStatus StatusMac(string label)
    {
        var plistPath = MacPlistPath(label);
        var fileExists = File.Exists(plistPath);
        var (_, output) = Run("launchctl", $"list {label}", ignoreFailure: true);
        var loaded = !string.IsNullOrWhiteSpace(output) && !output.Contains("Could not find service");
        return new AutostartStatus(Installed: fileExists, Loaded: loaded, Detail: output.Trim());
    }

    // ---------- Windows ----------

    // Backend: HKCU\Software\Microsoft\Windows\CurrentVersion\Run via reg.exe.
    // Earlier impl used Task Scheduler — schtasks /sc onlogon required admin
    // and produced visible console flashes during the welcome flow. HKCU Run
    // is per-user, writable without elevation, and the de-facto standard for
    // consumer Win apps.
    //
    // Mapping to AutostartStatus on Win: Installed = Loaded = registry value
    // exists. There's no disabled-but-present state in the Run key (the
    // Task Manager → Startup apps toggle lives in a separate StartupApproved
    // tree owned by user-facing UX, not us).
    private const string WindowsRunKey = "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    private static AutostartResult InstallWindows(
        string label,
        string programPath,
        IReadOnlyList<string> programArgs)
    {
        // reg.exe /d expects the data string. Quote the exe path and append
        // any args so logon launches the binary with the right command line.
        var sb = new StringBuilder();
        sb.Append('"').Append(programPath).Append('"');
        foreach (var a in programArgs)
            sb.Append(' ').Append(a);
        var commandLine = sb.ToString();

        // reg.exe quoting: wrap the /d value in double quotes; embedded
        // quotes get escaped as \".
        var escapedData = commandLine.Replace("\"", "\\\"");
        var args = $"add \"{WindowsRunKey}\" /v \"{label}\" /t REG_SZ /d \"{escapedData}\" /f";
        var (code, output) = Run("reg.exe", args);
        if (code != 0)
            return new AutostartResult(false, $"reg add failed: {output}");

        return new AutostartResult(true, $"Registered in HKCU Run key as '{label}'.");
    }

    private static AutostartResult UninstallWindows(string label)
    {
        var (code, output) = Run("reg.exe", $"delete \"{WindowsRunKey}\" /v \"{label}\" /f", ignoreFailure: true);
        // reg delete returns non-zero when the value is absent — treat as success.
        if (code != 0 && (output.Contains("unable to find", StringComparison.OrdinalIgnoreCase) ||
                          output.Contains("system was unable", StringComparison.OrdinalIgnoreCase)))
            return new AutostartResult(true, "Not installed; nothing to do.");
        return new AutostartResult(code == 0, output);
    }

    private static AutostartStatus StatusWindows(string label)
    {
        var (code, output) = Run("reg.exe", $"query \"{WindowsRunKey}\" /v \"{label}\"", ignoreFailure: true);
        // reg query exits 0 + prints the label + REG_SZ when present.
        var present = code == 0 && output.Contains(label, StringComparison.OrdinalIgnoreCase);
        return new AutostartStatus(present, present, output.Trim());
    }

    // No disabled-but-present state in HKCU Run — Disable == Uninstall.
    private static AutostartResult DisableWindows(string label) => UninstallWindows(label);

    // ---------- helpers ----------

    private static (int ExitCode, string Output) Run(string fileName, string args, bool ignoreFailure = false)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Explicit: suppress the console window that would otherwise flash
            // when launching reg.exe / launchctl / etc. from a GUI app.
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (-1, $"Failed to start: {fileName}");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var combined = (stdout + (string.IsNullOrEmpty(stderr) ? "" : "\n" + stderr)).Trim();
            return (p.ExitCode, combined);
        }
        catch (Exception ex)
        {
            return (-1, $"{fileName} could not start: {ex.Message}");
        }
    }

    private static string EscapeXml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}

public sealed record AutostartResult(bool Success, string Message, string? Path = null)
{
    public static AutostartResult Unsupported() =>
        new(false, "Autostart is supported on macOS and Windows only.");
}

public sealed record AutostartStatus(bool Installed, bool Loaded, string Detail);
