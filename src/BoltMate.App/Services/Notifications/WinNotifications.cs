using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BoltMate.App.Services;

/// <summary>
/// Windows-only toast permission probe. Windows 10/11 stores per-AUMID
/// toast settings at HKCU\Software\Microsoft\Windows\CurrentVersion\
/// Notifications\Settings\&lt;AUMID&gt;\Enabled (DWORD). Default is enabled
/// when the key doesn't exist. There is no "request authorisation" API
/// equivalent — the first toast fire is the user's introduction; they
/// can disable from the toast itself or via Settings.
/// </summary>
internal static class WinNotifications
{
    private const string AumidKey =
        @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\BoltMate";

    [SupportedOSPlatform("windows")]
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AumidKey);
            if (key is null) return true;                       // never configured → default enabled
            var value = key.GetValue("Enabled");
            if (value is null) return true;                     // key but no value → default enabled
            return Convert.ToInt32(value) != 0;
        }
        catch
        {
            // Registry access can throw under locked-down policies — assume
            // enabled and let the actual toast fire to surface any issue.
            return true;
        }
    }
}
