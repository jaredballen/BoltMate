using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BoltMate.App.Services;

/// <summary>
/// Windows toast permission probe + persistence. Windows 10/11 stores
/// per-AUMID toast settings at
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\&lt;AUMID&gt;\Enabled</c>
/// (DWORD). We treat <i>both</i> the OS Settings panel and our own
/// Settings → About → Notifications toggle as owners of that value —
/// writes from either side land in the same registry slot, so each
/// reflects the other on the next probe.
/// </summary>
internal static class WinNotifications
{
    private const string AumidKey =
        @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\BoltMate";
    private const string EnabledValue = "Enabled";

    /// <summary>Tri-state — mirrors the Mac UNAuthorizationStatus we expose elsewhere.</summary>
    public enum Status
    {
        /// <summary>Registry entry doesn't exist — Win has never recorded a per-app setting yet.</summary>
        NotDetermined = 0,
        /// <summary>User (or our welcome flow) flipped the toggle off.</summary>
        Denied        = 1,
        /// <summary>User has approved BoltMate notifications.</summary>
        Authorized    = 2,
    }

    [SupportedOSPlatform("windows")]
    public static Status GetStatus()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AumidKey);
            if (key is null) return Status.NotDetermined;       // key absent → never asked
            var value = key.GetValue(EnabledValue);
            if (value is null) return Status.NotDetermined;     // value absent → never asked
            return Convert.ToInt32(value) != 0 ? Status.Authorized : Status.Denied;
        }
        catch
        {
            // Registry access can throw under locked-down policies — treat
            // as NotDetermined so the welcome flow at least shows the primer
            // and the user can self-resolve.
            return Status.NotDetermined;
        }
    }

    /// <summary>
    /// Writes the per-AUMID Enabled DWORD. Creates the parent key (and
    /// any missing intermediate keys) when absent so the OS Settings
    /// panel picks BoltMate up in its list on next refresh.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool WriteEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AumidKey, writable: true);
            if (key is null) return false;
            key.SetValue(EnabledValue, enabled ? 1 : 0, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
