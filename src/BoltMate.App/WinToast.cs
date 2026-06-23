using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace BoltMate.App;

/// <summary>
/// Windows toast surface. Targets the Microsoft.Toolkit.Uwp.Notifications
/// path proved too heavy for our net10.0 (TFM-agnostic) build — it needs
/// a Windows-flavoured TFM to resolve the .Show() extension. PowerShell is
/// the simplest portable alternative: spawn a one-shot powershell.exe that
/// invokes <c>Windows.UI.Notifications.ToastNotificationManager</c> via the
/// built-in WinRT projection. No package dependency, no AUMID setup, works
/// on every Win 10/11.
/// </summary>
/// <remarks>
/// Per-toast cost is ~80 ms (powershell.exe cold start). Acceptable for
/// the once-per-condition health alerts that drive this path; if we ever
/// start firing toasts at sub-second cadence we'll want a long-running
/// helper process or a real WinRT-targeted build.
/// </remarks>
internal static class WinToast
{
    [SupportedOSPlatform("windows")]
    public static bool TryPost(string title, string body)
    {
        try
        {
            // Escape ' for the PowerShell single-quoted string literal.
            var t = title.Replace("'", "''");
            var b = body.Replace("'", "''");

            // WinRT projection: pull the ToastGeneric template, swap in our
            // title/body, queue the toast against the BoltMate AUMID. The
            // AUMID we use ("BoltMate") doesn't have to be pre-registered —
            // Windows accepts it and uses the app name fallback.
            var script =
                "$ErrorActionPreference='Stop';" +
                "[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]>$null;" +
                "$tpl=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
                "$tn=$tpl.GetElementsByTagName('text');" +
                $"$tn.Item(0).AppendChild($tpl.CreateTextNode('{t}'))>$null;" +
                $"$tn.Item(1).AppendChild($tpl.CreateTextNode('{b}'))>$null;" +
                "$toast=[Windows.UI.Notifications.ToastNotification]::new($tpl);" +
                "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('BoltMate').Show($toast);";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script.Replace("\"", "\\\"")}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            // Fire-and-forget within reason — short timeout so a wedged
            // powershell.exe can't block the calling tick. The toast
            // typically renders before powershell exits but we don't
            // depend on it.
            return p.WaitForExit(2000);
        }
        catch
        {
            return false;
        }
    }
}
