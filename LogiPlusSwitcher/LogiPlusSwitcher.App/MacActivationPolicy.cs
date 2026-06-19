using System;
using System.Runtime.InteropServices;

namespace LogiPlusSwitcher.App;

/// <summary>
/// Flips the macOS NSApplication activation policy so the Dock icon appears
/// while a window is visible and hides again when the app returns to
/// menubar-only mode. Equivalent to setting <c>LSUIElement</c> dynamically.
/// </summary>
/// <remarks>
/// No-op on non-macOS platforms. Uses Objective-C runtime calls because
/// Avalonia doesn't expose the NSApplication activation policy.
/// References:
///   https://developer.apple.com/documentation/appkit/nsapplicationactivationpolicy
/// </remarks>
internal static class MacActivationPolicy
{
    // NSApplicationActivationPolicyRegular = 0  (dock icon, menubar focus)
    // NSApplicationActivationPolicyAccessory = 1  (no dock, but can have windows)
    // NSApplicationActivationPolicyProhibited = 2

    private const int Regular = 0;
    private const int Accessory = 1;

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr ObjCGetClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMessage_get(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern bool SendMessage_setPolicy(IntPtr receiver, IntPtr selector, long policy);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendMessage_activate(IntPtr receiver, IntPtr selector, bool flag);

    /// <summary>Bring the app's Dock icon up; bring it to the foreground.</summary>
    public static void ShowDockIcon()
    {
        if (!OperatingSystem.IsMacOS()) return;
        TrySetPolicy(Regular);
        Activate();
    }

    /// <summary>Hide the Dock icon (return to menubar-only).</summary>
    public static void HideDockIcon()
    {
        if (!OperatingSystem.IsMacOS()) return;
        TrySetPolicy(Accessory);
    }

    private static void TrySetPolicy(int policy)
    {
        try
        {
            var nsApplicationClass = ObjCGetClass("NSApplication");
            if (nsApplicationClass == IntPtr.Zero) return;
            var sharedApp = SendMessage_get(nsApplicationClass, SelRegisterName("sharedApplication"));
            if (sharedApp == IntPtr.Zero) return;
            SendMessage_setPolicy(sharedApp, SelRegisterName("setActivationPolicy:"), policy);
        }
        catch
        {
            // Swallow — best-effort UX nicety; ok to skip if AppKit isn't available.
        }
    }

    private static void Activate()
    {
        try
        {
            var nsApplicationClass = ObjCGetClass("NSApplication");
            if (nsApplicationClass == IntPtr.Zero) return;
            var sharedApp = SendMessage_get(nsApplicationClass, SelRegisterName("sharedApplication"));
            if (sharedApp == IntPtr.Zero) return;
            SendMessage_activate(sharedApp, SelRegisterName("activateIgnoringOtherApps:"), true);
        }
        catch
        {
            // Swallow.
        }
    }
}
