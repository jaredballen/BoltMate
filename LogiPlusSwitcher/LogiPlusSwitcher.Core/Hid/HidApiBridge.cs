using System.Reflection;
using System.Runtime.InteropServices;

namespace LogiPlusSwitcher.Core.Hid;

/// <summary>
/// Native-library bootstrap and macOS-specific configuration for libhidapi.
/// </summary>
/// <remarks>
/// Two responsibilities:
/// 1. Pre-load <c>libhidapi.dylib</c> from a known install path on macOS
///    (Homebrew puts it in <c>/opt/homebrew/lib</c> on Apple Silicon and
///    <c>/usr/local/lib</c> on Intel — neither is on dlopen's default search
///    path). HidApi.Net registers its own DllImportResolver on its assembly so
///    we cannot override it — but a subsequent <c>dlopen("libhidapi.dylib")</c>
///    matches the already-loaded image by leafname, so the pre-load is enough.
/// 2. Switch the macOS backend to non-exclusive opens so other processes
///    (Logi Options+) keep the device open simultaneously. libhidapi defaults
///    to exclusive opens since 0.12; <c>hid_darwin_set_open_exclusive(0)</c>
///    reverts to <c>kIOHIDOptionsTypeNone</c>.
/// </remarks>
public static class HidApiBridge
{
    private const string LibraryName = "hidapi";

    private static readonly object Gate = new();
    private static bool _initialised;
    private static IntPtr _preloadedHandle;

    [DllImport(LibraryName, EntryPoint = "hid_darwin_set_open_exclusive", CallingConvention = CallingConvention.Cdecl)]
    private static extern int hid_darwin_set_open_exclusive(int open_exclusive);

    /// <summary>
    /// Pre-loads libhidapi from a well-known path and installs a resolver on
    /// this assembly so our direct P/Invoke (hid_darwin_set_open_exclusive)
    /// finds the same image. Idempotent; safe to call from any constructor.
    /// </summary>
    public static void EnsureNativeLibraryResolver()
    {
        lock (Gate)
        {
            if (_initialised)
                return;
            _initialised = true;
        }

        // Register on OUR assembly only — HidApi.Net guards its own assembly
        // and throws if we try to replace its resolver. The pre-load below is
        // what makes HidApi.Net's resolver succeed.
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveHidApi);

        foreach (var candidate in CandidatePaths())
        {
            if (NativeLibrary.TryLoad(candidate, out _preloadedHandle))
                return;
        }
    }

    private static IntPtr ResolveHidApi(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("hidapi", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        if (_preloadedHandle != IntPtr.Zero)
            return _preloadedHandle;

        foreach (var candidate in CandidatePaths())
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                _preloadedHandle = handle;
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/opt/homebrew/lib/libhidapi.dylib";        // Apple Silicon brew
            yield return "/usr/local/lib/libhidapi.dylib";           // Intel brew
            yield return "libhidapi.dylib";                          // App-local (DYLD search)
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            yield return "libhidapi-hidraw.so.0";                    // Preferred raw backend on Linux
            yield return "libhidapi-libusb.so.0";
            yield return "libhidapi.so.0";
            yield return "libhidapi.so";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return "hidapi.dll";
        }
    }

    /// <summary>
    /// Switches the macOS backend to non-exclusive opens so other processes
    /// (Logi Options+) can keep the device open simultaneously. Idempotent;
    /// safe to call on any platform.
    /// </summary>
    /// <returns>True if the call succeeded on macOS, false otherwise.</returns>
    public static bool SetMacOsNonExclusive()
    {
        EnsureNativeLibraryResolver();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return false;

        try
        {
            hid_darwin_set_open_exclusive(0);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
