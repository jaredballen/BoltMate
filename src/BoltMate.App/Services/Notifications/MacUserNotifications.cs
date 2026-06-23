using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Services;

/// <summary>
/// macOS-only — thin ObjC bridge around UNUserNotificationCenter.
/// Replaces the deprecated NSUserNotification path so we can drive
/// the real OS authorisation prompt + query auth status. Loads the
/// UserNotifications framework via dlopen on first use and exposes
/// three calls:
///   • <see cref="GetAuthorizationStatus"/> — synchronous status probe
///     (drives a semaphore around the framework's async
///     <c>getNotificationSettings</c>).
///   • <see cref="RequestAuthorizationAsync"/> — fires the OS prompt
///     on first call (NotDetermined → Authorized/Denied). Subsequent
///     calls are idempotent.
///   • <see cref="DeliverAsync"/> — schedules a UNNotificationRequest
///     with the given title + body, auth-gated by the OS so denied
///     status drops silently.
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

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string UserNotificationsFramework =
        "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";

    [DllImport(LibSystem)] private static extern IntPtr dlopen(string path, int mode);
    private const int RTLD_NOW = 2;

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr GetSelector(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr sel);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send_str(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.LPStr)] string s);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send_obj(IntPtr receiver, IntPtr sel, IntPtr a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send_obj_obj(IntPtr receiver, IntPtr sel, IntPtr a1, IntPtr a2);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send_obj_obj_obj(IntPtr receiver, IntPtr sel, IntPtr a1, IntPtr a2, IntPtr a3);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern long Send_long(IntPtr receiver, IntPtr sel);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send_ulong_block(IntPtr receiver, IntPtr sel, ulong arg, IntPtr block);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send_block(IntPtr receiver, IntPtr sel, IntPtr block);

    // ---------------------------------------------------------------
    // Block ABI — minimal manual layout matching libBlocksRuntime's
    // shape. Stack-allocated for short-lived completion handlers.
    // ---------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
        public IntPtr Context; // GCHandle.ToIntPtr — we stash the callback delegate here
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public ulong Reserved;
        public ulong Size;
    }

    [DllImport(LibSystem, EntryPoint = "memcpy")]
    private static extern IntPtr Memcpy(IntPtr dest, IntPtr src, IntPtr count);

    private static IntPtr _nsConcreteStackBlock;
    private static readonly BlockDescriptor _blockDescriptor = new()
    {
        Reserved = 0,
        Size = (ulong)Marshal.SizeOf<BlockLiteral>(),
    };
    private static IntPtr _blockDescriptorPtr;

    // Keep delegate references alive across the unmanaged callback.
    private static readonly RequestAuthInvoke _requestAuthInvoke = RequestAuthCallback;
    private static readonly GetSettingsInvoke _getSettingsInvoke = GetSettingsCallback;
    private static readonly AddCompletionInvoke _addCompletionInvoke = AddCompletionCallback;

    private delegate void RequestAuthInvoke(IntPtr block, byte granted, IntPtr error);
    private delegate void GetSettingsInvoke(IntPtr block, IntPtr settings);
    private delegate void AddCompletionInvoke(IntPtr block, IntPtr error);

    private static bool _initialised;
    private static readonly object _initLock = new();

    private static bool TryInit()
    {
        if (_initialised) return true;
        lock (_initLock)
        {
            if (_initialised) return true;
            try
            {
                if (dlopen(UserNotificationsFramework, RTLD_NOW) == IntPtr.Zero)
                {
                    Log.LogWarning("UserNotifications.framework load failed");
                    return false;
                }
                // _NSConcreteStackBlock is exported by libobjc on macOS — use dlopen
                // of libSystem then dlsym. The simpler shortcut: call dlopen with
                // a no-arg path to get the global handle.
                _nsConcreteStackBlock = NativeLibrary.GetExport(
                    NativeLibrary.Load(LibSystem), "_NSConcreteStackBlock");
                _blockDescriptorPtr = Marshal.AllocHGlobal(Marshal.SizeOf<BlockDescriptor>());
                Marshal.StructureToPtr(_blockDescriptor, _blockDescriptorPtr, false);
                _initialised = true;
                return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "MacUserNotifications init failed");
                return false;
            }
        }
    }

    private static IntPtr NSString(string s)
    {
        var nsString = GetClass("NSString");
        return Send_str(nsString, GetSelector("stringWithUTF8String:"), s);
    }

    public static AuthorizationStatus GetAuthorizationStatus()
    {
        if (!TryInit()) return AuthorizationStatus.NotDetermined;
        var resetEvent = new ManualResetEventSlim(false);
        long status = (long)AuthorizationStatus.NotDetermined;
        void Callback(IntPtr settings)
        {
            try
            {
                if (settings != IntPtr.Zero)
                {
                    var sel = GetSelector("authorizationStatus");
                    status = Send_long(settings, sel);
                }
            }
            finally { resetEvent.Set(); }
        }
        if (!InvokeGetSettings(Callback))
        {
            return AuthorizationStatus.NotDetermined;
        }
        // 2-second cap on the wait — UN center calls back on its own queue,
        // typically in < 50ms. If something has wedged we'd rather return
        // NotDetermined than block the caller's UI thread indefinitely.
        if (!resetEvent.Wait(TimeSpan.FromSeconds(2)))
        {
            Log.LogWarning("getNotificationSettings timed out");
            return AuthorizationStatus.NotDetermined;
        }
        return (AuthorizationStatus)status;
    }

    public static Task<bool> RequestAuthorizationAsync(
        AuthorizationOptions options = AuthorizationOptions.Alert | AuthorizationOptions.Sound,
        CancellationToken ct = default)
    {
        if (!TryInit()) return Task.FromResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!InvokeRequestAuth((ulong)options, granted => tcs.TrySetResult(granted)))
        {
            tcs.TrySetResult(false);
        }
        ct.Register(() => tcs.TrySetResult(false));
        return tcs.Task;
    }

    public static bool Deliver(string title, string body)
    {
        if (!TryInit()) return false;
        try
        {
            var contentClass = GetClass("UNMutableNotificationContent");
            var content = Send(contentClass, GetSelector("alloc"));
            content = Send(content, GetSelector("init"));
            Send_obj(content, GetSelector("setTitle:"), NSString(title));
            Send_obj(content, GetSelector("setBody:"), NSString(body));

            var requestClass = GetClass("UNNotificationRequest");
            var identifier = NSString($"BoltMate-{Guid.NewGuid():N}");
            var request = Send_obj_obj_obj(
                requestClass,
                GetSelector("requestWithIdentifier:content:trigger:"),
                identifier, content, IntPtr.Zero);

            var centerClass = GetClass("UNUserNotificationCenter");
            var center = Send(centerClass, GetSelector("currentNotificationCenter"));

            // Pass nil completion handler — the call is fire-and-forget at our
            // level. If auth is denied the OS drops it; if it succeeds the
            // banner shows when the OS feels like it.
            Send_obj_obj(center, GetSelector("addNotificationRequest:withCompletionHandler:"),
                request, IntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "UN deliver threw");
            return false;
        }
    }

    // ---- Block plumbing ----------------------------------------------

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, Action<bool>> _authCallbacks = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, Action<IntPtr>> _settingsCallbacks = new();
    private static long _nextCallbackId;

    private static bool InvokeRequestAuth(ulong options, Action<bool> callback)
    {
        var centerClass = GetClass("UNUserNotificationCenter");
        var center = Send(centerClass, GetSelector("currentNotificationCenter"));
        if (center == IntPtr.Zero) return false;

        var id = (IntPtr)Interlocked.Increment(ref _nextCallbackId);
        _authCallbacks[id] = callback;

        var block = MakeBlock(Marshal.GetFunctionPointerForDelegate(_requestAuthInvoke), id);
        try
        {
            Send_ulong_block(center,
                GetSelector("requestAuthorizationWithOptions:completionHandler:"),
                options, block);
            return true;
        }
        catch (Exception ex)
        {
            _authCallbacks.TryRemove(id, out _);
            Log.LogWarning(ex, "requestAuthorization send failed");
            FreeBlock(block);
            return false;
        }
    }

    private static bool InvokeGetSettings(Action<IntPtr> callback)
    {
        var centerClass = GetClass("UNUserNotificationCenter");
        var center = Send(centerClass, GetSelector("currentNotificationCenter"));
        if (center == IntPtr.Zero) return false;

        var id = (IntPtr)Interlocked.Increment(ref _nextCallbackId);
        _settingsCallbacks[id] = callback;

        var block = MakeBlock(Marshal.GetFunctionPointerForDelegate(_getSettingsInvoke), id);
        try
        {
            Send_block(center,
                GetSelector("getNotificationSettingsWithCompletionHandler:"),
                block);
            return true;
        }
        catch (Exception ex)
        {
            _settingsCallbacks.TryRemove(id, out _);
            Log.LogWarning(ex, "getNotificationSettings send failed");
            FreeBlock(block);
            return false;
        }
    }

    private static IntPtr MakeBlock(IntPtr invoke, IntPtr context)
    {
        var literal = new BlockLiteral
        {
            Isa = _nsConcreteStackBlock,
            Flags = 0,
            Reserved = 0,
            Invoke = invoke,
            Descriptor = _blockDescriptorPtr,
            Context = context,
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<BlockLiteral>());
        Marshal.StructureToPtr(literal, ptr, false);
        return ptr;
    }

    private static void FreeBlock(IntPtr block)
    {
        try { Marshal.FreeHGlobal(block); } catch { /* idempotent */ }
    }

    private static void RequestAuthCallback(IntPtr block, byte granted, IntPtr error)
    {
        try
        {
            var literal = Marshal.PtrToStructure<BlockLiteral>(block);
            if (_authCallbacks.TryRemove(literal.Context, out var callback))
            {
                callback(granted != 0);
            }
        }
        catch (Exception ex) { Log.LogWarning(ex, "RequestAuthCallback threw"); }
        finally { FreeBlock(block); }
    }

    private static void GetSettingsCallback(IntPtr block, IntPtr settings)
    {
        try
        {
            var literal = Marshal.PtrToStructure<BlockLiteral>(block);
            if (_settingsCallbacks.TryRemove(literal.Context, out var callback))
            {
                callback(settings);
            }
        }
        catch (Exception ex) { Log.LogWarning(ex, "GetSettingsCallback threw"); }
        finally { FreeBlock(block); }
    }

    private static void AddCompletionCallback(IntPtr block, IntPtr error)
    {
        FreeBlock(block);
    }
}
