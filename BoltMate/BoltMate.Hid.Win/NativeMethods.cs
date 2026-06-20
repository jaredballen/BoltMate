using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BoltMate.Hid.Win;

/// <summary>
/// All Win32 P/Invoke declarations used by the native HID transport.
/// Split out so the transport / connection classes stay readable.
/// </summary>
/// <remarks>
/// References: hid.dll (HID class API), setupapi.dll (device enumeration),
/// kernel32.dll (file I/O + overlapped). Constants are taken from the
/// Windows SDK headers (hidsdi.h, setupapi.h, winnt.h, winbase.h).
/// </remarks>
internal static class NativeMethods
{
    // ---- GUIDs ----
    /// <summary>GUID_DEVINTERFACE_HID — the interface class GUID for HID devices.</summary>
    public static readonly Guid GuidDevInterfaceHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    // ---- SetupDi flags ----
    public const uint DIGCF_PRESENT = 0x02;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;

    // ---- CreateFile flags ----
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x01;
    public const uint FILE_SHARE_WRITE = 0x02;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    // ---- WaitForSingleObject return values ----
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;
    public const uint INFINITE = 0xFFFFFFFF;

    // ---- GetLastError values we care about ----
    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_DEVICE_NOT_CONNECTED = 1167;

    // ---- HidP / HidD report types (HidD_SetOutputReport uses no constant; raw byte buffer) ----
    public const uint HIDP_STATUS_SUCCESS = 0x00110000;

    // -------------------------------------------------------------------
    // setupapi.dll
    // -------------------------------------------------------------------

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,   // pass IntPtr.Zero on first call to learn required size
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    // -------------------------------------------------------------------
    // hid.dll
    // -------------------------------------------------------------------

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(SafeFileHandle hidHandle, ref HIDD_ATTRIBUTES attributes);

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle hidHandle, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        // Reserved fields ... not needed for our purposes but the struct must
        // match exactly otherwise HidP_GetCaps clobbers memory. The full
        // struct is documented in hidpi.h; we declare just enough.
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public ushort Reserved4;
        public ushort Reserved5;
        public ushort Reserved6;
        public ushort Reserved7;
        public ushort Reserved8;
        public ushort Reserved9;
        public ushort Reserved10;
        public ushort Reserved11;
        public ushort Reserved12;
        public ushort Reserved13;
        public ushort Reserved14;
        public ushort Reserved15;
        public ushort Reserved16;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool HidD_GetProductString(SafeFileHandle hidHandle, IntPtr buffer, uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool HidD_GetManufacturerString(SafeFileHandle hidHandle, IntPtr buffer, uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool HidD_GetSerialNumberString(SafeFileHandle hidHandle, IntPtr buffer, uint bufferLength);

    /// <summary>
    /// HID-class output-report write. Per task #31 / libusb-hidapi's Windows
    /// backend: <c>WriteFile</c> for long output reports can fail with
    /// ERROR_INVALID_FUNCTION on some HID devices (notably the Bolt receiver's
    /// management interface). <c>HidD_SetOutputReport</c> uses a different
    /// kernel path and often succeeds where WriteFile fails. We try this
    /// FIRST and fall back to WriteFile.
    /// </summary>
    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetOutputReport(SafeFileHandle hidHandle, IntPtr buffer, uint bufferLength);

    // -------------------------------------------------------------------
    // kernel32.dll — file I/O + overlapped
    // -------------------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        SafeFileHandle file,
        IntPtr buffer,
        uint bytesToRead,
        IntPtr bytesRead,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(
        SafeFileHandle file,
        IntPtr buffer,
        uint bytesToWrite,
        IntPtr bytesWritten,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(
        SafeFileHandle file,
        IntPtr overlapped,
        out uint bytesTransferred,
        bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr securityAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ResetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct NATIVE_OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint OffsetLow;
        public uint OffsetHigh;
        public IntPtr EventHandle;
    }
}
