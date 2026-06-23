using System.Runtime.InteropServices;
using BoltMate.Hid.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Hid.IOKit;

/// <summary>
/// macOS-only transport that bypasses libhidapi and talks to IOKit
/// (IOHIDManager + IOHIDDevice) directly. Required because libhidapi's
/// <c>hid_darwin_set_open_exclusive(0)</c> doesn't actually deliver shared
/// access on recent macOS — concurrent opens still fail with
/// <c>kIOReturnExclusiveAccess</c>, and worse, holding the management
/// interface open via libhidapi disables device-firmware button handling
/// (wheel-mode toggle, gesture buttons). IOKit-direct with explicit
/// <c>kIOHIDOptionsTypeNone</c> preserves shared access and firmware
/// behaviour.
/// </summary>
public sealed class IOKitReceiverTransport : IReceiverTransport, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<IOKitReceiverTransport> _logger;
    private readonly IntPtr _manager;
    // Permission gate. Returns true once the OS has granted Input
    // Monitoring. We never call IOHIDManagerOpen until it does, because
    // doing so under TCC=Unknown triggers the system prompt — which we
    // want to happen at a moment the wizard has just primed the user
    // for, not at process start.
    private readonly Func<bool> _isInputMonitoringGranted;
    private readonly IDisposable? _grantSubscription;
    private bool _managerOpen;
    private readonly object _openLock = new();

    public IOKitReceiverTransport(
        ILoggerFactory? loggerFactory = null,
        Func<bool>? isInputMonitoringGranted = null,
        IObservable<bool>? grantChanges = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("IOKitReceiverTransport is macOS-only.");

        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<IOKitReceiverTransport>();
        // No gate wired → assume granted (legacy / Cli / tests). Production
        // app wires _permissions.InputMonitoring.IsGranted at composition.
        _isInputMonitoringGranted = isInputMonitoringGranted ?? (static () => true);
        // Mid-run revoke handler. If the user toggles Input Monitoring off
        // in System Settings while we're running, IOHIDDeviceOpen calls
        // start returning kIOReturnNotPermitted and existing IOHIDDeviceRefs
        // become invalid. Close the manager so the next grant cleanly
        // re-opens; ReceiverManager's poll will see Enumerate return empty
        // in the meantime and dispose any standing connections.
        _grantSubscription = grantChanges?.Subscribe(granted =>
        {
            if (!granted) ForceClose();
        });

        _manager = IOKitInterop.IOHIDManagerCreate(IntPtr.Zero, IOKitInterop.OptionsNone);
        if (_manager == IntPtr.Zero)
            throw new InvalidOperationException("IOHIDManagerCreate failed");

        var match = IOKitInterop.CreateMatchingDictionary(
            BoltConstants.LogitechVendorId, BoltConstants.BoltReceiverProductId);
        IOKitInterop.IOHIDManagerSetDeviceMatching(_manager, match);
        IOKitInterop.CFRelease(match);

        _logger.LogInformation("IOKit transport initialised — IOHIDManagerOpen deferred until permission grant");
    }

    /// <summary>
    /// Lazy IOHIDManagerOpen — first call after the permission gate flips
    /// open performs the real open. Subsequent calls are no-ops. Also
    /// detects a sticky-open state where the gate has since closed (revoke
    /// mid-run) and closes the manager so a future grant gets a fresh open.
    /// </summary>
    private bool EnsureManagerOpen()
    {
        var granted = _isInputMonitoringGranted();
        if (_managerOpen)
        {
            if (granted) return true;
            // Granted then revoked — drop the open so the OS doesn't keep
            // feeding us reads on a stale handle.
            ForceClose();
            return false;
        }
        if (!granted) return false;
        lock (_openLock)
        {
            if (_managerOpen) return true;
            var openResult = IOKitInterop.IOHIDManagerOpen(_manager, IOKitInterop.OptionsNone);
            if (openResult != IOKitInterop.Success)
            {
                _logger.LogWarning("IOHIDManagerOpen returned 0x{Code:X8} — enumeration may still work", openResult);
            }
            _managerOpen = true;
            _logger.LogInformation("IOKit manager opened (permission granted)");
            return true;
        }
    }

    private void ForceClose()
    {
        lock (_openLock)
        {
            if (!_managerOpen) return;
            try { IOKitInterop.IOHIDManagerClose(_manager, IOKitInterop.OptionsNone); }
            catch { /* best-effort */ }
            _managerOpen = false;
            _logger.LogInformation("IOKit manager closed (permission revoked or torn down)");
        }
    }

    public IReadOnlyList<BoltReceiverInfo> Enumerate()
    {
        if (!EnsureManagerOpen()) return Array.Empty<BoltReceiverInfo>();
        var set = IOKitInterop.IOHIDManagerCopyDevices(_manager);
        if (set == IntPtr.Zero) return Array.Empty<BoltReceiverInfo>();

        try
        {
            var count = (int)IOKitInterop.CFSetGetCount(set);
            if (count == 0) return Array.Empty<BoltReceiverInfo>();

            var devices = new IntPtr[count];
            IOKitInterop.CFSetGetValues(set, devices);

            var result = new List<BoltReceiverInfo>();
            foreach (var dev in devices)
            {
                if (dev == IntPtr.Zero) continue;
                var usagePage = IOKitInterop.GetInt32Property(dev, "PrimaryUsagePage");
                var usage = IOKitInterop.GetInt32Property(dev, "PrimaryUsage");
                if (usagePage != BoltConstants.ManagementUsagePage) continue;
                if (usage != BoltConstants.ManagementUsage) continue;

                var locationId = IOKitInterop.GetInt32Property(dev, "LocationID") ?? 0;
                var product = IOKitInterop.GetStringProperty(dev, "Product") ?? "Bolt Receiver";
                var manufacturer = IOKitInterop.GetStringProperty(dev, "Manufacturer") ?? "Logitech";
                var serial = IOKitInterop.GetStringProperty(dev, "SerialNumber") ?? "";
                var release = (ushort)(IOKitInterop.GetInt32Property(dev, "VersionNumber") ?? 0);

                // Path encodes LocationID + the raw IOHIDDeviceRef pointer so
                // Open(info) can find this exact interface again. The pointer
                // remains valid while the IOHIDManager retains the device set.
                var path = $"iokit:{locationId:X8}:{dev.ToInt64():X16}";

                result.Add(new BoltReceiverInfo(path, serial, product, manufacturer, release));
            }
            return result;
        }
        finally { IOKitInterop.CFRelease(set); }
    }

    public IReceiverConnection Open(BoltReceiverInfo info)
    {
        if (!EnsureManagerOpen())
            throw new InvalidOperationException(
                "Input Monitoring permission not granted — cannot open IOKit device.");

        // Path format: "iokit:<locationId>:<deviceRef>". Re-enumerate to
        // find the live IOHIDDeviceRef matching that location, then open it.
        // (Stashing the pointer in the path string is fine within a single
        // process lifetime — but re-enumerating is more robust against
        // hotplug.)
        var set = IOKitInterop.IOHIDManagerCopyDevices(_manager);
        if (set == IntPtr.Zero)
            throw new InvalidOperationException("IOHIDManagerCopyDevices returned null");

        try
        {
            var count = (int)IOKitInterop.CFSetGetCount(set);
            var devices = new IntPtr[count];
            IOKitInterop.CFSetGetValues(set, devices);

            var parts = info.Path.Split(':');
            if (parts.Length != 3 || !uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var targetLocation))
                throw new ArgumentException($"Unrecognised IOKit path: {info.Path}");

            foreach (var dev in devices)
            {
                if (dev == IntPtr.Zero) continue;
                var location = IOKitInterop.GetInt32Property(dev, "LocationID") ?? 0;
                if ((uint)location != targetLocation) continue;
                var usagePage = IOKitInterop.GetInt32Property(dev, "PrimaryUsagePage");
                var usage = IOKitInterop.GetInt32Property(dev, "PrimaryUsage");
                if (usagePage != BoltConstants.ManagementUsagePage) continue;
                if (usage != BoltConstants.ManagementUsage) continue;

                var openResult = IOKitInterop.IOHIDDeviceOpen(dev, IOKitInterop.OptionsNone);
                if (openResult != IOKitInterop.Success)
                    throw new InvalidOperationException(
                        $"IOHIDDeviceOpen failed: 0x{openResult:X8} (0xE00002C5 = exclusive access; 0xE00002BC = no Input Monitoring permission)");

                _logger.LogInformation("Opened receiver {Product} via IOKit-direct (location 0x{Loc:X8})",
                    info.ProductString, location);
                return new IOKitReceiverConnection(dev, info.Path,
                    _loggerFactory.CreateLogger<IOKitReceiverConnection>());
            }

            throw new InvalidOperationException($"No matching IOHIDDevice for location 0x{targetLocation:X8}");
        }
        finally { IOKitInterop.CFRelease(set); }
    }

    public void Dispose()
    {
        _grantSubscription?.Dispose();
        if (_manager != IntPtr.Zero)
        {
            if (_managerOpen)
            {
                IOKitInterop.IOHIDManagerClose(_manager, IOKitInterop.OptionsNone);
            }
            IOKitInterop.CFRelease(_manager);
        }
    }
}
