using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Services;

/// <summary>
/// macOS notification surface — UNUserNotificationCenter via the
/// <c>libboltmate_un.dylib</c> sidecar. The dylib is a tiny Objective-C
/// translation unit (see <c>Native/Mac/boltmate_un.m</c>) that wraps
/// the three calls we need in plain C functions so the .NET host can
/// hit them through ordinary <see cref="DllImport"/>. Blocks live
/// entirely on the Objective-C side, which sidesteps the libdispatch /
/// foreign-thread reverse-P/Invoke instability we kept hitting when we
/// tried to construct blocks from C#.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal static class MacUserNotifications
{
    public enum AuthorizationStatus
    {
        NotDetermined = 0,
        Denied        = 1,
        Authorized    = 2,
        Provisional   = 3,
        Ephemeral     = 4,
    }

    [Flags]
    public enum AuthorizationOptions : ulong
    {
        Badge = 1,
        Sound = 2,
        Alert = 4,
    }

    public static ILogger Log { get; set; } = NullLogger.Instance;

    // The dylib is staged into Contents/MacOS/ next to the host binary
    // by Directory.Build.targets. dlopen's default search includes that
    // directory when launched via the .app bundle, so this base name
    // resolves without an absolute path.
    private const string Lib = "libboltmate_un";

    [DllImport(Lib, EntryPoint = "bm_un_get_status")]
    private static extern int NativeGetStatus();

    [DllImport(Lib, EntryPoint = "bm_un_request_authorization")]
    private static extern int NativeRequestAuthorization(int options);

    [DllImport(Lib, EntryPoint = "bm_un_deliver", CharSet = CharSet.Ansi)]
    private static extern int NativeDeliver(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body);

    /// <summary>
    /// Synchronous status probe. Returns <see cref="AuthorizationStatus.NotDetermined"/>
    /// on any error so callers can fall through to the "ask the user"
    /// path.
    /// </summary>
    public static AuthorizationStatus GetAuthorizationStatus()
    {
        try
        {
            var raw = NativeGetStatus();
            if (raw < 0) return AuthorizationStatus.NotDetermined;
            return (AuthorizationStatus)raw;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "bm_un_get_status threw");
            return AuthorizationStatus.NotDetermined;
        }
    }

    /// <summary>
    /// Drives the OS modal Allow / Don't Allow prompt. Sync over async at
    /// the native layer — the sidecar semaphore-waits on the OS callback.
    /// Wrapped in <see cref="Task.Run"/> here so the caller can await
    /// without blocking the UI thread; the modal can sit on screen for
    /// many seconds while the user decides.
    /// </summary>
    public static Task<bool> RequestAuthorizationAsync(
        AuthorizationOptions options = AuthorizationOptions.Alert | AuthorizationOptions.Sound,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                var raw = NativeRequestAuthorization((int)options);
                return raw == 1;
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "bm_un_request_authorization threw");
                return false;
            }
        }, ct);
    }

    /// <summary>
    /// Fires a UNNotificationRequest. Auth-gated by the OS — denied
    /// posts drop silently, no exception here. Returns false only on
    /// argument / encoding failure.
    /// </summary>
    public static bool Deliver(string title, string body)
    {
        try
        {
            return NativeDeliver(title ?? "", body ?? "") == 1;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "bm_un_deliver threw");
            return false;
        }
    }
}
