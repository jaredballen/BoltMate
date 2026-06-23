using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Services;

/// <summary>
/// macOS-only — subscribes to NSWorkspace.didWakeNotification and fires
/// <see cref="WakeFromSleep"/> after the machine wakes. Used by
/// PermissionsService as a reactive trigger because TCC state
/// occasionally flips across a sleep/wake cycle and we want to re-poll
/// immediately rather than wait out the backstop interval.
/// </summary>
/// <remarks>
/// Singleton-shaped (static event + Start/Stop) rather than instance-
/// based because NSWorkspace's notification center is process-global.
/// </remarks>
internal static class MacWakeNotifier
{
    private static ILogger _log = NullLogger.Instance;
    private static IntPtr _delegate;
    private static bool _started;
    private static readonly object _lock = new();

    public static event Action? WakeFromSleep;

    private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    [DllImport(ObjCRuntime, EntryPoint = "objc_getClass")]
    private static extern IntPtr ObjCGetClass(string name);

    [DllImport(ObjCRuntime, EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName(string name);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjCMsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjCMsgSend(IntPtr receiver, IntPtr selector,
        IntPtr a1, IntPtr a2, IntPtr a3, IntPtr a4);

    [DllImport(ObjCRuntime, EntryPoint = "objc_allocateClassPair")]
    private static extern IntPtr ObjCAllocateClassPair(IntPtr parent, string name, IntPtr extraBytes);

    [DllImport(ObjCRuntime, EntryPoint = "objc_registerClassPair")]
    private static extern void ObjCRegisterClassPair(IntPtr cls);

    [DllImport(ObjCRuntime, EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool ClassAddMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport(Foundation, EntryPoint = "NSStringFromClass")]
    private static extern IntPtr NSStringFromClass(IntPtr cls); // unused, kept for symbol-load sanity

    private delegate void WakeCallbackDelegate(IntPtr self, IntPtr cmd, IntPtr notification);

    public static void Start(ILogger? logger = null)
    {
        if (!OperatingSystem.IsMacOS()) return;
        lock (_lock)
        {
            if (_started) return;
            _log = logger ?? NullLogger.Instance;

            try
            {
                // 1. Define a tiny ObjC class with one selector that calls
                //    our managed callback. Subclass NSObject.
                var nsObject = ObjCGetClass("NSObject");
                var cls = ObjCAllocateClassPair(nsObject, "BoltMateWakeObserver", IntPtr.Zero);
                if (cls == IntPtr.Zero)
                {
                    _log.LogWarning("MacWakeNotifier: objc_allocateClassPair failed");
                    return;
                }
                WakeCallbackDelegate callback = OnWakeNotification;
                _callbackKeepAlive = callback; // prevent GC of the delegate
                var sel = SelRegisterName("onWakeNotification:");
                var imp = Marshal.GetFunctionPointerForDelegate(callback);
                ClassAddMethod(cls, sel, imp, "v@:@");
                ObjCRegisterClassPair(cls);

                // 2. Instantiate the observer.
                _delegate = ObjCMsgSend(ObjCMsgSend(cls, SelRegisterName("alloc")),
                                        SelRegisterName("init"));

                // 3. Register against [NSWorkspace.sharedWorkspace.notificationCenter]
                //    for NSWorkspaceDidWakeNotification.
                var nsWorkspace = ObjCGetClass("NSWorkspace");
                var workspace = ObjCMsgSend(nsWorkspace, SelRegisterName("sharedWorkspace"));
                var center = ObjCMsgSend(workspace, SelRegisterName("notificationCenter"));

                // Notification name = constant string symbol — load via dlsym from AppKit.
                var notifName = LoadStringConstant(AppKit, "NSWorkspaceDidWakeNotification");
                if (notifName == IntPtr.Zero)
                {
                    _log.LogWarning("MacWakeNotifier: NSWorkspaceDidWakeNotification symbol not found");
                    return;
                }

                ObjCMsgSend(center,
                    SelRegisterName("addObserver:selector:name:object:"),
                    _delegate, sel, notifName, IntPtr.Zero);

                _started = true;
                _log.LogInformation("MacWakeNotifier: subscribed to NSWorkspaceDidWakeNotification");
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "MacWakeNotifier: Start failed (non-fatal — backstop poll still active)");
            }
        }
    }

    public static void Stop()
    {
        lock (_lock)
        {
            if (!_started) return;
            _started = false;
            try
            {
                var nsWorkspace = ObjCGetClass("NSWorkspace");
                var workspace = ObjCMsgSend(nsWorkspace, SelRegisterName("sharedWorkspace"));
                var center = ObjCMsgSend(workspace, SelRegisterName("notificationCenter"));
                ObjCMsgSend(center, SelRegisterName("removeObserver:"), _delegate, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            catch { /* best-effort */ }
            _delegate = IntPtr.Zero;
            _callbackKeepAlive = null;
        }
    }

    private static WakeCallbackDelegate? _callbackKeepAlive;

    private static void OnWakeNotification(IntPtr self, IntPtr cmd, IntPtr notification)
    {
        try { WakeFromSleep?.Invoke(); }
        catch (Exception ex) { _log.LogDebug(ex, "MacWakeNotifier: subscriber threw"); }
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern IntPtr dlsym(IntPtr handle, string symbol);

    private const int RTLD_LAZY = 1;

    private static IntPtr LoadStringConstant(string framework, string symbol)
    {
        var handle = dlopen(framework, RTLD_LAZY);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        var addr = dlsym(handle, symbol);
        if (addr == IntPtr.Zero) return IntPtr.Zero;
        // The symbol is a pointer to an NSString* — read the pointer value.
        return Marshal.ReadIntPtr(addr);
    }
}
