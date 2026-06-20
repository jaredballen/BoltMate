using System.Runtime.InteropServices;
using LogiPlusSwitcher.Hid.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace LogiPlusSwitcher.Hid.Win;

/// <summary>
/// Native Win32 HID transport — uses <c>setupapi.dll</c> for enumeration and
/// <c>hid.dll</c> + <c>CreateFile</c>/<c>ReadFile</c>/<c>WriteFile</c> for I/O.
/// No native DLL dependency (Windows ships hid.dll + setupapi.dll
/// universally). Drops the HidApi.Net + bundled hidapi.dll path that
/// suffered task-#31 long-report write failures and arm64-vs-x64
/// emulation woes.
/// </summary>
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

    /// <summary>
    /// Enumerates HID devices via SetupAPI, filters to the Bolt receiver's
    /// management interface (VID 0x046D, PID 0xC548, usage page 0xFF00,
    /// usage 0x0001).
    /// </summary>
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

        var results = new List<BoltReceiverInfo>();
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

                // Open with neither GENERIC_READ nor GENERIC_WRITE so we can
                // query attributes without claiming exclusive use — some HID
                // drivers reject CreateFile with write access just for
                // metadata reads. CreateFile with desiredAccess=0 is allowed.
                using var probeHandle = NativeMethods.CreateFileW(
                    devicePath, 0,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
                if (probeHandle.IsInvalid)
                    continue;

                var attrs = new NativeMethods.HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<NativeMethods.HIDD_ATTRIBUTES>() };
                if (!NativeMethods.HidD_GetAttributes(probeHandle, ref attrs)) continue;
                if (attrs.VendorID != BoltConstants.LogitechVendorId) continue;
                if (attrs.ProductID != BoltConstants.BoltReceiverProductId) continue;

                // Filter to the management interface by top-level collection
                // usage page / usage. The Bolt enumerates multiple HID
                // collections (keyboard, mouse, digitizer, management); we
                // only want 0xFF00 / 0x0001.
                if (!NativeMethods.HidD_GetPreparsedData(probeHandle, out var preparsed)) continue;
                ushort usagePage, usage, inputLen;
                try
                {
                    var caps = new NativeMethods.HIDP_CAPS();
                    var status = NativeMethods.HidP_GetCaps(preparsed, ref caps);
                    if (status != unchecked((int)NativeMethods.HIDP_STATUS_SUCCESS)) continue;
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inputLen = caps.InputReportByteLength;
                }
                finally { NativeMethods.HidD_FreePreparsedData(preparsed); }

                // TEMP: log every Bolt-matching collection we see so we can map
                // which collection carries short vs long HID++ reports.
                _logger.LogInformation(
                    "Bolt collection probed: usagePage=0x{Up:X4} usage=0x{Use:X4} inputReportLen={Len} path={Path}",
                    usagePage, usage, inputLen, devicePath);

                if (usagePage != BoltConstants.ManagementUsagePage) continue;
                if (usage != BoltConstants.ManagementUsage) continue;

                var product = ReadHidString(probeHandle, NativeMethods.HidD_GetProductString) ?? "Bolt Receiver";
                var manufacturer = ReadHidString(probeHandle, NativeMethods.HidD_GetManufacturerString) ?? "Logitech";
                var serial = ReadHidString(probeHandle, NativeMethods.HidD_GetSerialNumberString) ?? "";

                results.Add(new BoltReceiverInfo(devicePath, serial, product, manufacturer, attrs.VersionNumber));
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return results;
    }

    public IReceiverConnection Open(BoltReceiverInfo info)
    {
        var handle = NativeMethods.CreateFileW(
            info.Path,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new InvalidOperationException($"CreateFile on {info.Path} failed: 0x{err:X}");
        }

        // Read-buffer sizing: the Bolt management interface declares the
        // SHORT report (7 bytes) but the receiver actually delivers BOTH 0x10
        // short and 0x11 long (20 bytes) reports on the same interface, plus
        // 0x20 DJ reports. The descriptor's InputReportByteLength only
        // reflects the maximum length the descriptor explicitly enumerates;
        // we've observed it returning 7 even though 20-byte 0x11 reports
        // arrive in the wild. To be safe — and match the IOKit transport's
        // pre-sized buffer — we always allocate 64 bytes for reads. Windows'
        // HID class driver delivers whatever size the actual report is and
        // sets bytesRead accordingly.
        ushort declaredInputReportLength = 0;
        if (NativeMethods.HidD_GetPreparsedData(handle, out var preparsed))
        {
            try
            {
                var caps = new NativeMethods.HIDP_CAPS();
                if (NativeMethods.HidP_GetCaps(preparsed, ref caps) == unchecked((int)NativeMethods.HIDP_STATUS_SUCCESS))
                    declaredInputReportLength = caps.InputReportByteLength;
            }
            finally { NativeMethods.HidD_FreePreparsedData(preparsed); }
        }
        const ushort safeReadBuffer = 64;
        var inputReportLength = (ushort)Math.Max(declaredInputReportLength, safeReadBuffer);

        _logger.LogInformation("Opened receiver {Product} via native Win HID (declared input report = {Declared}, read buffer = {Buf})",
            info.ProductString, declaredInputReportLength, inputReportLength);
        return new WinReceiverConnection(handle, info.Path, inputReportLength,
            _loggerFactory.CreateLogger<WinReceiverConnection>());
    }

    public void Dispose() { /* no global state to release */ }

    // ---------------------------------------------------------------

    private static string? GetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref NativeMethods.SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        // First call: size discovery.
        NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifaceData,
            IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W's first field is `cbSize`.
            // For 64-bit packing, the value is 8 (4-byte cbSize + 2-byte
            // alignment + the first WCHAR). x86 packs to 6. Both are
            // documented quirks of the W variant.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifaceData,
                buffer, required, out _, IntPtr.Zero))
                return null;

            // The DevicePath field starts at offset 4 (after cbSize).
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private delegate bool HidStringFunc(SafeFileHandle handle, IntPtr buffer, uint bufferLength);

    private static string? ReadHidString(SafeFileHandle handle, HidStringFunc func)
    {
        // 254 is the documented maximum for HidD_Get*String.
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
