using System.Runtime.InteropServices;
using BoltMate.Hid.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace BoltMate.Hid.Win;

/// <summary>
/// Native Win32 HID transport. Uses <c>setupapi.dll</c> for enumeration and
/// <c>hid.dll</c> + <c>CreateFile</c>/<c>ReadFile</c>/<c>WriteFile</c> for I/O.
/// No bundled native dependency (Windows ships hid.dll + setupapi.dll
/// universally). Drops HidApi.Net + bundled hidapi.dll.
/// </summary>
/// <remarks>
/// Bolt receiver topology on Windows: the management interface enumerates
/// as TWO HID collections under the same physical interface (MI_02):
/// <c>0xFF00/0x0001</c> (Col01) carries 7-byte SHORT HID++ reports (0x10),
/// <c>0xFF00/0x0002</c> (Col02) carries 20-byte LONG HID++ reports (0x11).
/// macOS/Linux see this as one interface; Windows splits it. We open BOTH
/// collections, merge the read streams, and route writes by report ID.
/// </remarks>
public sealed class WinReceiverTransport : IReceiverTransport, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WinReceiverTransport> _logger;

    public WinReceiverTransport(ILoggerFactory? loggerFactory = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("WinReceiverTransport is Windows-only.");

        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<WinReceiverTransport>();
        _logger.LogInformation("Win HID transport initialised (native setupapi + hid.dll)");
    }

    public IReadOnlyList<BoltReceiverInfo> Enumerate()
    {
        var hidGuid = NativeMethods.GuidDevInterfaceHid;
        var deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
            ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);

        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1)
        {
            _logger.LogWarning("SetupDiGetClassDevs failed: 0x{Err:X}", Marshal.GetLastWin32Error());
            return Array.Empty<BoltReceiverInfo>();
        }

        // Group management-interface collections by their physical-device
        // instance id so we can pair col01 (short reports) with col02 (long
        // reports) belonging to the SAME receiver. Path format:
        //   \\?\HID#VID_046D&PID_C548&MI_02&Col01#8&3ee8091&0&0000#{4d1e55b2-...}
        // The "8&3ee8091&0" segment is the receiver's instance id — shared
        // across both collections of that receiver. We key by that.
        var byInstance = new Dictionary<string, ReceiverPaths>(StringComparer.OrdinalIgnoreCase);
        string? lastProduct = null, lastManufacturer = null, lastSerial = null;
        ushort lastVersion = 0;

        try
        {
            var idx = 0u;
            while (true)
            {
                var ifaceData = new NativeMethods.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DATA>(),
                };
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, idx, ref ifaceData))
                    break;
                idx++;

                var devicePath = GetDeviceInterfaceDetail(deviceInfoSet, ref ifaceData);
                if (devicePath is null) continue;

                using var probe = NativeMethods.CreateFileW(
                    devicePath, 0,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
                if (probe.IsInvalid) continue;

                var attrs = new NativeMethods.HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<NativeMethods.HIDD_ATTRIBUTES>() };
                if (!NativeMethods.HidD_GetAttributes(probe, ref attrs)) continue;
                if (attrs.VendorID != BoltConstants.LogitechVendorId) continue;
                if (attrs.ProductID != BoltConstants.BoltReceiverProductId) continue;

                if (!NativeMethods.HidD_GetPreparsedData(probe, out var preparsed)) continue;
                ushort usagePage, usage, inputLen;
                try
                {
                    var caps = new NativeMethods.HIDP_CAPS();
                    if (NativeMethods.HidP_GetCaps(preparsed, ref caps) != unchecked((int)NativeMethods.HIDP_STATUS_SUCCESS))
                        continue;
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inputLen = caps.InputReportByteLength;
                }
                finally { NativeMethods.HidD_FreePreparsedData(preparsed); }

                // Only management-interface collections (0xFF00).
                if (usagePage != BoltConstants.ManagementUsagePage) continue;

                // Strip the per-collection Col0X suffix to get the receiver's
                // physical instance id. Path lowercased for stable dict key.
                var instanceId = ExtractInstanceId(devicePath);
                if (instanceId is null) continue;

                if (!byInstance.TryGetValue(instanceId, out var paths))
                {
                    paths = new ReceiverPaths();
                    byInstance[instanceId] = paths;

                    // Read metadata once per receiver (it's identical across
                    // its collections).
                    lastProduct = ReadHidString(probe, NativeMethods.HidD_GetProductString) ?? "Bolt Receiver";
                    lastManufacturer = ReadHidString(probe, NativeMethods.HidD_GetManufacturerString) ?? "Logitech";
                    lastSerial = ReadHidString(probe, NativeMethods.HidD_GetSerialNumberString) ?? "";
                    lastVersion = attrs.VersionNumber;
                    paths.Product = lastProduct;
                    paths.Manufacturer = lastManufacturer;
                    paths.Serial = lastSerial;
                    paths.Version = lastVersion;
                }

                if (usage == BoltConstants.ManagementUsage) // 0x0001 — short reports
                    paths.ShortPath = devicePath;
                else if (usage == 0x0002)                   // long reports
                    paths.LongPath = devicePath;

                _logger.LogDebug("Bolt collection: usage 0x{Up:X4}/0x{Use:X4} inputLen={Len} path={Path}",
                    usagePage, usage, inputLen, devicePath);
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        var results = new List<BoltReceiverInfo>();
        foreach (var (instance, paths) in byInstance)
        {
            if (paths.ShortPath is null)
            {
                _logger.LogWarning("Bolt receiver {Inst} missing 0xFF00/0x0001 short-report collection — skipping",
                    instance);
                continue;
            }
            if (paths.LongPath is null)
            {
                _logger.LogWarning("Bolt receiver {Inst} missing 0xFF00/0x0002 long-report collection — short HID++ only",
                    instance);
            }

            // Combined path encoding: "<short>||<long-or-empty>".
            var combinedPath = paths.ShortPath + "||" + (paths.LongPath ?? "");
            results.Add(new BoltReceiverInfo(combinedPath,
                paths.Serial ?? "", paths.Product ?? "Bolt Receiver",
                paths.Manufacturer ?? "Logitech", paths.Version));
        }

        return results;
    }

    public IReceiverConnection Open(BoltReceiverInfo info)
    {
        var parts = info.Path.Split("||", 2);
        var shortPath = parts[0];
        var longPath = parts.Length == 2 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : null;

        var shortHandle = NativeMethods.CreateFileW(shortPath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (shortHandle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            shortHandle.Dispose();
            throw new InvalidOperationException($"CreateFile on short-report collection {shortPath} failed: 0x{err:X}");
        }

        SafeFileHandle? longHandle = null;
        if (longPath is not null)
        {
            longHandle = NativeMethods.CreateFileW(longPath,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (longHandle.IsInvalid)
            {
                _logger.LogWarning("CreateFile on long-report collection {Path} failed: 0x{Err:X} — long HID++ disabled",
                    longPath, Marshal.GetLastWin32Error());
                longHandle.Dispose();
                longHandle = null;
            }
        }

        _logger.LogInformation("Opened receiver {Product} (short collection {Short}, long collection {Long})",
            info.ProductString, "ok",
            longHandle is null ? "missing" : "ok");

        return new WinReceiverConnection(shortHandle, longHandle, info.Path,
            _loggerFactory.CreateLogger<WinReceiverConnection>());
    }

    public void Dispose() { /* no global state */ }

    // ---------------------------------------------------------------

    private sealed class ReceiverPaths
    {
        public string? ShortPath; // 0xFF00/0x0001 — 7-byte input, report id 0x10
        public string? LongPath;  // 0xFF00/0x0002 — 20-byte input, report id 0x11
        public string? Product;
        public string? Manufacturer;
        public string? Serial;
        public ushort Version;
    }

    /// <summary>Extracts the receiver's physical-device instance id from a HID path.</summary>
    /// <remarks>
    /// Sample path:
    ///   \\?\HID#VID_046D&PID_C548&MI_02&Col01#8&3ee8091&0&0000#{guid}
    /// We want "8&3ee8091&0" — the segment after the second '#'. Per-collection
    /// trailing "&NNNN" varies (0000 for col01, 0001 for col02) but the
    /// prefix matches.
    /// </remarks>
    private static string? ExtractInstanceId(string path)
    {
        var lower = path.ToLowerInvariant();
        var firstHash = lower.IndexOf('#');
        if (firstHash < 0) return null;
        var secondHash = lower.IndexOf('#', firstHash + 1);
        if (secondHash < 0) return null;
        var thirdHash = lower.IndexOf('#', secondHash + 1);
        if (thirdHash < 0) return null;
        var instance = lower.Substring(secondHash + 1, thirdHash - secondHash - 1);
        // Strip the trailing "&NNNN" (per-collection counter).
        var lastAmp = instance.LastIndexOf('&');
        if (lastAmp > 0) instance = instance.Substring(0, lastAmp);
        return instance;
    }

    private static string? GetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref NativeMethods.SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifaceData,
            IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifaceData,
                buffer, required, out _, IntPtr.Zero))
                return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private delegate bool HidStringFunc(SafeFileHandle handle, IntPtr buffer, uint bufferLength);

    private static string? ReadHidString(SafeFileHandle handle, HidStringFunc func)
    {
        const int max = 254;
        var buf = Marshal.AllocHGlobal(max);
        try
        {
            if (!func(handle, buf, max)) return null;
            var s = Marshal.PtrToStringUni(buf);
            return string.IsNullOrEmpty(s) ? null : s.TrimEnd('\0', ' ');
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
