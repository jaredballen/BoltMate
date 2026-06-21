using System;
using System.Runtime.InteropServices;

namespace BoltMate.App;

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

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendMessage_setStr(IntPtr receiver, IntPtr selector, IntPtr str);

    /// <summary>
    /// Rewrites the existing app menu's first-item title — the one that shows
    /// after the Apple logo in the macOS menubar. Call AFTER Avalonia has
    /// constructed NSApp.mainMenu (any time post framework init is fine).
    /// </summary>
    public static void SetAppMenuTitle(string title)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var nsApplicationClass = ObjCGetClass("NSApplication");
            if (nsApplicationClass == IntPtr.Zero) return;
            var sharedApp = SendMessage_get(nsApplicationClass, SelRegisterName("sharedApplication"));
            if (sharedApp == IntPtr.Zero) return;
            var mainMenu = SendMessage_get(sharedApp, SelRegisterName("mainMenu"));
            if (mainMenu == IntPtr.Zero) return;
            var firstItem = SendMessage_getItemAt(mainMenu, SelRegisterName("itemAtIndex:"), 0);
            if (firstItem == IntPtr.Zero) return;
            // The first item itself has a title, but the actual displayed
            // string is the title of its SUBMENU. Set both for safety.
            var nsStringClass = ObjCGetClass("NSString");
            if (nsStringClass == IntPtr.Zero) return;
            var nsTitle = SendMessage_stringWithUtf8(nsStringClass, title);
            if (nsTitle == IntPtr.Zero) return;
            SendMessage_setStr(firstItem, SelRegisterName("setTitle:"), nsTitle);
            var subMenu = SendMessage_get(firstItem, SelRegisterName("submenu"));
            if (subMenu != IntPtr.Zero)
                SendMessage_setStr(subMenu, SelRegisterName("setTitle:"), nsTitle);
        }
        catch
        {
            // Swallow — cosmetic.
        }
    }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMessage_getItemAt(IntPtr receiver, IntPtr selector, long index);

    /// <summary>
    /// Sets <c>NSProcessInfo.processName</c> so the macOS menubar's application
    /// menu shows the right title when launched without a .app bundle (e.g.
    /// <c>dotnet run</c>). The bundled .app's Info.plist provides this in
    /// production; this fixes the dev experience.
    /// </summary>
    public static void SetProcessName(string name)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var nsProcessInfoClass = ObjCGetClass("NSProcessInfo");
            if (nsProcessInfoClass == IntPtr.Zero) return;
            var processInfo = SendMessage_get(nsProcessInfoClass, SelRegisterName("processInfo"));
            if (processInfo == IntPtr.Zero) return;

            var nsStringClass = ObjCGetClass("NSString");
            if (nsStringClass == IntPtr.Zero) return;
            var nsName = SendMessage_stringWithUtf8(nsStringClass, name);
            if (nsName == IntPtr.Zero) return;

            SendMessage_setStr(processInfo, SelRegisterName("setProcessName:"), nsName);
        }
        catch
        {
            // Swallow — cosmetic.
        }
    }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMessage_stringWithUtf8_impl(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPStr)] string utf8);

    private static IntPtr SendMessage_stringWithUtf8(IntPtr nsStringClass, string s) =>
        SendMessage_stringWithUtf8_impl(nsStringClass, SelRegisterName("stringWithUTF8String:"), s);

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
