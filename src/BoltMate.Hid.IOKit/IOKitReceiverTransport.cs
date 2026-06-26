using System.Runtime.InteropServices;
using BoltMate.Hid.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Hid.IOKit;

/// <summary>
/// macOS-only transport that talks to IOKit (IOHIDManager + IOHIDDevice)
/// directly. Required because libhidapi's <c>hid_darwin_set_open_exclusive(0)</c>
/// no longer delivers shared access on recent macOS, and holding the
/// management interface via libhidapi disables device-firmware buttons.
/// IOKit-direct with <c>kIOHIDOptionsTypeNone</c> preserves shared access
/// and firmware behaviour.
///
/// HOT-PLUG STRATEGY — see reference_iohidmanager_threading memory for the
/// full story. We've conclusively failed to use IOHIDManager hot-plug
/// callbacks from .NET (every attempt crashes with
/// <c>os_unfair_lock is corrupt</c>). So instead: a fresh IOHIDManager is
/// created, opened, and CopyDevices'd on every Enumerate call. No
/// persistent manager, no scheduling, no callbacks. The assumption being
/// tested is that a brand-new just-opened manager reports current device
/// state (vs the stale snapshot a long-lived one returns). If true,
/// hot-plug detection works at poll-interval granularity (~2s).
/// </summary>
public sealed class IOKitReceiverTransport : IReceiverTransport, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<IOKitReceiverTransport> _logger;
    private readonly Func<bool> _isInputMonitoringGranted;
    private readonly IDisposable? _grantSubscription;
    private bool _disposed;

    public IOKitReceiverTransport(
        ILoggerFactory? loggerFactory = null,
        Func<bool>? isInputMonitoringGranted = null,
        IObservable<bool>? grantChanges = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("IOKitReceiverTransport is macOS-only.");

        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<IOKitReceiverTransport>();
        _isInputMonitoringGranted = isInputMonitoringGranted ?? (static () => true);
        // grantChanges is no longer load-bearing (no persistent manager to
        // close), but kept for API compat. A subscription that logs the
        // transition is enough.
        _grantSubscription = grantChanges?.Subscribe(granted =>
            _logger.LogInformation("Input Monitoring grant changed → {Granted}", granted));

        _logger.LogInformation("IOKit transport initialised (fresh-manager-per-poll mode)");
    }

    /// <summary>
    /// Build a fresh IOHIDManager, open it, set Bolt-receiver matching,
    /// run the body with the device set, then tear down. The manager only
    /// exists for the duration of the body.
    /// </summary>
    private T WithFreshManager<T>(Func<IntPtr[], T> body, T fallback)
    {
        if (!_isInputMonitoringGranted()) return fallback;

        var manager = IOKitInterop.IOHIDManagerCreate(IntPtr.Zero, IOKitInterop.OptionsNone);
        if (manager == IntPtr.Zero)
        {
            _logger.LogWarning("IOHIDManagerCreate failed");
            return fallback;
        }

        try
        {
            var match = IOKitInterop.CreateMatchingDictionary(
                BoltConstants.LogitechVendorId, BoltConstants.BoltReceiverProductId);
            IOKitInterop.IOHIDManagerSetDeviceMatching(manager, match);
            IOKitInterop.CFRelease(match);

            var openResult = IOKitInterop.IOHIDManagerOpen(manager, IOKitInterop.OptionsNone);
            if (openResult != IOKitInterop.Success)
            {
                _logger.LogWarning("IOHIDManagerOpen returned 0x{Code:X8}", openResult);
                // Still try CopyDevices — sometimes it works even when Open
                // returned non-success (e.g. 0xE00002C5 exclusive access).
            }

            var set = IOKitInterop.IOHIDManagerCopyDevices(manager);
            if (set == IntPtr.Zero) return fallback;

            try
            {
                var count = (int)IOKitInterop.CFSetGetCount(set);
                if (count == 0) return body(Array.Empty<IntPtr>());

                var devices = new IntPtr[count];
                IOKitInterop.CFSetGetValues(set, devices);
                return body(devices);
            }
            finally { IOKitInterop.CFRelease(set); }
        }
        finally
        {
            try { IOKitInterop.IOHIDManagerClose(manager, IOKitInterop.OptionsNone); } catch { }
            try { IOKitInterop.CFRelease(manager); } catch { }
        }
    }

    public IReadOnlyList<BoltReceiverInfo> Enumerate()
    {
        return WithFreshManager(devices =>
        {
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

                // Path keyed on LocationID ONLY. The IOHIDDeviceRef pointer
                // is unstable across our fresh-manager-per-poll cycles (new
                // manager → new ref → different value every 2s). If the path
                // changes between polls, ReceiverManager treats it as
                // remove+add → tears down the open connection →
                // DeviceEnricher restarts feature reads → names/battery
                // never stabilise. LocationID is a USB bus-stable identifier
                // for the same physical port; two receivers can't share it
                // at the same time. Open() re-enumerates to find the live
                // ref for that LocationID.
                var path = $"iokit:{locationId:X8}";

                result.Add(new BoltReceiverInfo(path, serial, product, manufacturer, release));
            }
            return (IReadOnlyList<BoltReceiverInfo>)result;
        }, Array.Empty<BoltReceiverInfo>());
    }

    public IReceiverConnection Open(BoltReceiverInfo info)
    {
        if (!_isInputMonitoringGranted())
            throw new InvalidOperationException(
                "Input Monitoring permission not granted — cannot open IOKit device.");

        var parts = info.Path.Split(':');
        if (parts.Length < 2 || !uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var targetLocation))
            throw new ArgumentException($"Unrecognised IOKit path: {info.Path}");

        // Re-enumerate fresh, find the device by LocationID, CFRetain the
        // ref (so it survives the manager's release), then IOHIDDeviceOpen
        // on the retained ref. The IOKitReceiverConnection takes ownership
        // and CFReleases on dispose.
        IntPtr retainedRef = IntPtr.Zero;
        WithFreshManager<int>(devices =>
        {
            foreach (var dev in devices)
            {
                if (dev == IntPtr.Zero) continue;
                var location = IOKitInterop.GetInt32Property(dev, "LocationID") ?? 0;
                if ((uint)location != targetLocation) continue;
                var usagePage = IOKitInterop.GetInt32Property(dev, "PrimaryUsagePage");
                var usage = IOKitInterop.GetInt32Property(dev, "PrimaryUsage");
                if (usagePage != BoltConstants.ManagementUsagePage) continue;
                if (usage != BoltConstants.ManagementUsage) continue;

                IOKitInterop.CFRetain(dev);
                retainedRef = dev;
                break;
            }
            return 0;
        }, 0);

        if (retainedRef == IntPtr.Zero)
            throw new InvalidOperationException($"No matching IOHIDDevice for location 0x{targetLocation:X8}");

        var openResult = IOKitInterop.IOHIDDeviceOpen(retainedRef, IOKitInterop.OptionsNone);
        if (openResult != IOKitInterop.Success)
        {
            try { IOKitInterop.CFRelease(retainedRef); } catch { }
            throw new InvalidOperationException(
                $"IOHIDDeviceOpen failed: 0x{openResult:X8} (0xE00002C5 = exclusive access; 0xE00002BC = no Input Monitoring permission)");
        }

        _logger.LogInformation("Opened receiver {Product} via IOKit-direct (location 0x{Loc:X8})",
            info.ProductString, targetLocation);
        return new IOKitReceiverConnection(retainedRef, info.Path,
            _loggerFactory.CreateLogger<IOKitReceiverConnection>());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _grantSubscription?.Dispose();
    }
}
