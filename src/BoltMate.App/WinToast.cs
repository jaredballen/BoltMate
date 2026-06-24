using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App;

/// <summary>
/// Windows toast surface. Spawns a PowerShell child that invokes the
/// WinRT projection of <c>Windows.UI.Notifications.ToastNotificationManager</c>
/// against our AUMID ("BoltMate" — registered by the installer's
/// Start Menu shortcut). The script lives on disk next to our exe so
/// PowerShell parsing isn't a fragile inline string, and we read
/// stderr on the failure path so a misbehaving toast pipeline is
/// loud, not silent.
/// </summary>
internal static class WinToast
{
    public static ILogger Log { get; set; } = NullLogger.Instance;

    [SupportedOSPlatform("windows")]
    public static bool TryPost(string title, string body)
    {
        try
        {
            var script = ResolveScriptPath();
            if (script is null)
            {
                Log.LogWarning("WinToast: post-toast.ps1 not found alongside binary");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList =
                {
                    "-NoLogo", "-NoProfile", "-NonInteractive",
                    "-WindowStyle", "Hidden",
                    "-ExecutionPolicy", "Bypass",
                    "-File", script,
                    "-Title", title,
                    "-Body", body,
                },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { }
                Log.LogWarning("WinToast: powershell.exe timed out after 5s");
                return false;
            }
            var stderr = p.StandardError.ReadToEnd();
            var stdout = p.StandardOutput.ReadToEnd();
            if (p.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
            {
                Log.LogWarning("WinToast: powershell exit={Exit} stderr={Stderr} stdout={Stdout}",
                    p.ExitCode, stderr.Trim(), stdout.Trim());
                return false;
            }
            Log.LogDebug("WinToast: posted title={Title}", title);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "WinToast.TryPost threw");
            return false;
        }
    }

    private static string? ResolveScriptPath()
    {
        // The script ships in Native/Win/ relative to the binary. AppContext
        // .BaseDirectory is the right anchor here: it returns the apphost
        // directory for both framework-dependent AND self-contained
        // single-file publishes. Assembly.GetEntryAssembly().Location
        // returns "" for single-file, so anchoring on that resolves to a
        // relative path that depends on the current working directory —
        // which for a tray app launched from Start Menu is not the install
        // dir, so File.Exists fails and TryPost silently bails. Lesson
        // learned: always BaseDirectory for asset lookups in published apps.
        var path = Path.Combine(AppContext.BaseDirectory, "Native", "Win", "post-toast.ps1");
        return File.Exists(path) ? path : null;
    }
}
