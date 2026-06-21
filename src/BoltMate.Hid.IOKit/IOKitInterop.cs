using System;
using System.Runtime.InteropServices;

namespace BoltMate.Hid.IOKit;

/// <summary>
/// Native macOS IOKit + Core Foundation interop for opening IOHIDDevice
/// instances directly with explicit options — bypassing libhidapi's
/// hid_open_path which appears to ignore hid_darwin_set_open_exclusive(0)
/// on recent macOS versions (Sequoia / Sonoma) and seize the device anyway.
/// </summary>
/// <remarks>
/// Only used on macOS. Win + Linux paths stick with the libhidapi transport.
/// </remarks>
public static class IOKitInterop
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>kIOHIDOptionsTypeNone — open with shared access (other processes can also open).</summary>
    public const uint OptionsNone = 0;

    /// <summary>kIOHIDOptionsTypeSeizeDevice — open exclusively, blocking other openers.</summary>
    public const uint OptionsSeize = 1;

    /// <summary>kIOReturnSuccess.</summary>
    public const int Success = 0;

    // ---------- Core Foundation ----------

    [DllImport(CoreFoundation)]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPStr)] string cStr, uint encoding);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFNumberCreate(IntPtr alloc, int theType, ref int valuePtr);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFDictionaryCreateMutable(IntPtr alloc, IntPtr capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundation)]
    public static extern void CFDictionarySetValue(IntPtr dict, IntPtr key, IntPtr value);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFSetGetValues(IntPtr theSet, IntPtr[] values);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFSetGetCount(IntPtr theSet);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFStringGetCStringPtr(IntPtr str, uint encoding);

    [DllImport(CoreFoundation)]
    public static extern int CFStringGetCString(IntPtr str, IntPtr buffer, IntPtr bufferSize, uint encoding);

    // CFNumber types
    public const int CFNumberSInt32Type = 3;
    public const uint EncodingUTF8 = 0x08000100;

    // ---------- IOKit / IOHIDManager ----------

    [DllImport(IOKit)]
    public static extern IntPtr IOHIDManagerCreate(IntPtr allocator, uint options);

    [DllImport(IOKit)]
    public static extern void IOHIDManagerSetDeviceMatching(IntPtr manager, IntPtr matchingDict);

    [DllImport(IOKit)]
    public static extern int IOHIDManagerOpen(IntPtr manager, uint options);

    [DllImport(IOKit)]
    public static extern int IOHIDManagerClose(IntPtr manager, uint options);

    [DllImport(IOKit)]
    public static extern IntPtr IOHIDManagerCopyDevices(IntPtr manager);

    // ---------- IOKit / IOHIDDevice ----------

    [DllImport(IOKit)]
    public static extern int IOHIDDeviceOpen(IntPtr device, uint options);

    [DllImport(IOKit)]
    public static extern int IOHIDDeviceClose(IntPtr device, uint options);

    [DllImport(IOKit)]
    public static extern IntPtr IOHIDDeviceGetProperty(IntPtr device, IntPtr key);

    // ---------- IOKit / Run loop + I/O ----------

    public delegate void IOHIDReportCallback(IntPtr context, int result, IntPtr sender,
        uint reportType, uint reportId, IntPtr report, IntPtr reportLength);

    [DllImport(IOKit)]
    public static extern void IOHIDDeviceRegisterInputReportCallback(IntPtr device,
        IntPtr report, IntPtr reportLength, IOHIDReportCallback callback, IntPtr context);

    [DllImport(IOKit)]
    public static extern void IOHIDDeviceScheduleWithRunLoop(IntPtr device,
        IntPtr runLoop, IntPtr runLoopMode);

    [DllImport(IOKit)]
    public static extern void IOHIDDeviceUnscheduleFromRunLoop(IntPtr device,
        IntPtr runLoop, IntPtr runLoopMode);

    [DllImport(IOKit)]
    public static extern int IOHIDDeviceSetReport(IntPtr device, uint reportType,
        IntPtr reportID, IntPtr report, IntPtr reportLength);

    // ---------- Core Foundation run loop ----------

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopRun();

    [DllImport(CoreFoundation)]
    public static extern void CFRunLoopStop(IntPtr rl);

    /// <summary>kCFRunLoopDefaultMode — symbol from CoreFoundation. Loaded via dlsym.</summary>
    public static IntPtr CFRunLoopDefaultMode { get; } = LoadCFSymbol("kCFRunLoopDefaultMode");

    private static IntPtr LoadCFSymbol(string name)
    {
        var handle = dlopen(CoreFoundation, 2 /* RTLD_NOW */);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        var sym = dlsym(handle, name);
        return sym == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(sym);
    }

    [DllImport("/usr/lib/libdl.dylib")]
    private static extern IntPtr dlopen([MarshalAs(UnmanagedType.LPStr)] string filename, int flags);

    [DllImport("/usr/lib/libdl.dylib")]
    private static extern IntPtr dlsym(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string symbol);

    /// <summary>kIOHIDReportTypeOutput — for outbound writes via IOHIDDeviceSetReport.</summary>
    public const uint ReportTypeOutput = 1;

    // ---------- Helpers ----------

    /// <summary>
    /// Builds a CFMutableDictionary with { kIOHIDVendorIDKey: vendorId, kIOHIDProductIDKey: productId }.
    /// Caller must CFRelease the returned dict.
    /// </summary>
    public static IntPtr CreateMatchingDictionary(ushort vendorId, ushort productId)
    {
        var dict = CFDictionaryCreateMutable(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        var vidKey = CFStringCreateWithCString(IntPtr.Zero, "VendorID", EncodingUTF8);
        var pidKey = CFStringCreateWithCString(IntPtr.Zero, "ProductID", EncodingUTF8);

        var vid = (int)vendorId;
        var pid = (int)productId;
        var vidVal = CFNumberCreate(IntPtr.Zero, CFNumberSInt32Type, ref vid);
        var pidVal = CFNumberCreate(IntPtr.Zero, CFNumberSInt32Type, ref pid);

        CFDictionarySetValue(dict, vidKey, vidVal);
        CFDictionarySetValue(dict, pidKey, pidVal);

        CFRelease(vidKey);
        CFRelease(pidKey);
        CFRelease(vidVal);
        CFRelease(pidVal);

        return dict;
    }

    /// <summary>Reads a string-valued IOHIDDevice property by key name (e.g. "Product", "SerialNumber"). Returns null if absent.</summary>
    public static string? GetStringProperty(IntPtr device, string keyName)
    {
        var key = CFStringCreateWithCString(IntPtr.Zero, keyName, EncodingUTF8);
        try
        {
            var value = IOHIDDeviceGetProperty(device, key);
            if (value == IntPtr.Zero) return null;
            var ptr = CFStringGetCStringPtr(value, EncodingUTF8);
            if (ptr != IntPtr.Zero)
                return Marshal.PtrToStringUTF8(ptr);
            var buf = Marshal.AllocHGlobal(256);
            try
            {
                if (CFStringGetCString(value, buf, (IntPtr)256, EncodingUTF8) != 0)
                    return Marshal.PtrToStringUTF8(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }
            return null;
        }
        finally { CFRelease(key); }
    }

    /// <summary>Reads a 32-bit numeric IOHIDDevice property. Returns null if absent.</summary>
    public static int? GetInt32Property(IntPtr device, string keyName)
    {
        var key = CFStringCreateWithCString(IntPtr.Zero, keyName, EncodingUTF8);
        try
        {
            var value = IOHIDDeviceGetProperty(device, key);
            if (value == IntPtr.Zero) return null;
            int result = 0;
            if (CFNumberGetValue(value, CFNumberSInt32Type, ref result))
                return result;
            return null;
        }
        finally { CFRelease(key); }
    }

    [DllImport(CoreFoundation)]
    private static extern bool CFNumberGetValue(IntPtr number, int theType, ref int valuePtr);
}
