using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using BoltMate.Hid.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Hid.IOKit;

/// <summary>
/// Watches USB attach/detach events for the Logitech Bolt receiver
/// (VID 0x046D, PID 0xC548) via IOKit's
/// <c>IOServiceAddMatchingNotification</c>. Each event signals
/// <see cref="Changes"/>; subscribers re-poll the transport on the signal.
///
/// Why USB-level, not HID-level? IOHIDManager hot-plug callbacks crash
/// from .NET (see reference_iohidmanager_threading memory — four
/// architecturally-distinct attempts, all crashed with
/// <c>os_unfair_lock is corrupt</c>). USB notifications use a different
/// IOKit object class (<c>io_service_t</c>, not <c>IOHIDDeviceRef</c>)
/// with different internal locking. The callback here does ZERO IOKit
/// property reads — it only drains the iterator (required by API to arm
/// the next notification) and signals managed state. The actual device
/// enumeration happens elsewhere, on a different thread, via the
/// proven-safe fresh-manager-per-Enumerate path.
/// </summary>
public sealed class UsbBoltNotifier : IDisposable
{
    private readonly ILogger<UsbBoltNotifier> _logger;
    private readonly Subject<Unit> _changes = new();
    private IntPtr _notifyPort;
    private uint _addedIter;
    private uint _removedIter;
    private Thread? _runLoopThread;
    private IntPtr _runLoop;
    private readonly ManualResetEventSlim _ready = new(false);
    // Held to keep marshalled function pointers alive for the life of the port.
    private IOKitInterop.IOServiceMatchingCallback? _addedCallback;
    private IOKitInterop.IOServiceMatchingCallback? _removedCallback;
    private bool _disposed;
    private readonly object _gate = new();

    /// <summary>Signal stream — fires (with no payload) on each USB
    /// attach OR detach event for VID 0x046D / PID 0xC548.</summary>
    public IObservable<Unit> Changes => _changes.AsObservable();

    public UsbBoltNotifier(ILogger<UsbBoltNotifier>? logger = null)
    {
        _logger = logger ?? NullLogger<UsbBoltNotifier>.Instance;
    }

    /// <summary>Spawns the runloop thread + arms the notifications.
    /// Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_runLoopThread is { IsAlive: true }) return;
            if (_disposed) return;

            _ready.Reset();
            _runLoopThread = new Thread(RunLoopThreadProc)
            {
                IsBackground = true,
                Name = "BoltMate.USB.HotPlug",
            };
            _runLoopThread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
                _logger.LogWarning("USB notifier did not signal ready within 5s");
        }
    }

    private void RunLoopThreadProc()
    {
        try
        {
            _runLoop = IOKitInterop.CFRunLoopGetCurrent();
            _notifyPort = IOKitInterop.IONotificationPortCreate(IntPtr.Zero);
            if (_notifyPort == IntPtr.Zero)
            {
                _logger.LogError("IONotificationPortCreate failed");
                _ready.Set();
                return;
            }

            var source = IOKitInterop.IONotificationPortGetRunLoopSource(_notifyPort);
            IOKitInterop.CFRunLoopAddSource(_runLoop, source, IOKitInterop.CFRunLoopDefaultMode);

            _addedCallback = OnAdded;
            _removedCallback = OnRemoved;

            // Separate matching dictionaries per AddMatching call —
            // IOServiceAddMatchingNotification consumes its CFDictionary.
            var addedMatch = BuildUsbMatching();
            var addedResult = IOKitInterop.IOServiceAddMatchingNotification(
                _notifyPort, IOKitInterop.IOServiceFirstMatch, addedMatch,
                _addedCallback, IntPtr.Zero, out _addedIter);
            if (addedResult != IOKitInterop.Success)
                _logger.LogWarning("IOServiceAddMatchingNotification (FirstMatch) → 0x{Code:X8}", addedResult);
            // Drain to arm future notifications. The "FirstMatch" iterator
            // is pre-populated with currently-attached devices; we don't
            // care about those (the transport enumerates them on first
            // Enumerate call), we just want the drain to enable the kqueue
            // for future events.
            DrainIterator(_addedIter);

            var removedMatch = BuildUsbMatching();
            var removedResult = IOKitInterop.IOServiceAddMatchingNotification(
                _notifyPort, IOKitInterop.IOServiceTerminate, removedMatch,
                _removedCallback, IntPtr.Zero, out _removedIter);
            if (removedResult != IOKitInterop.Success)
                _logger.LogWarning("IOServiceAddMatchingNotification (Terminate) → 0x{Code:X8}", removedResult);
            DrainIterator(_removedIter);

            _ready.Set();
            _logger.LogInformation("USB notifier armed (VID 0x046D / PID 0xC548)");

            IOKitInterop.CFRunLoopRun();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USB notifier runloop crashed");
            _ready.Set();
        }
    }

    private static IntPtr BuildUsbMatching()
    {
        // Returns a CFMutableDictionary matching IOUSBDevice. We add
        // idVendor + idProduct constraints to narrow to Bolt-only.
        var dict = IOKitInterop.IOServiceMatching(IOKitInterop.IOUSBDeviceClassName);
        if (dict == IntPtr.Zero) return IntPtr.Zero;

        var vidKey = IOKitInterop.CFStringCreateWithCString(IntPtr.Zero, "idVendor", IOKitInterop.EncodingUTF8);
        var pidKey = IOKitInterop.CFStringCreateWithCString(IntPtr.Zero, "idProduct", IOKitInterop.EncodingUTF8);
        int vid = BoltConstants.LogitechVendorId;
        int pid = BoltConstants.BoltReceiverProductId;
        var vidVal = IOKitInterop.CFNumberCreate(IntPtr.Zero, IOKitInterop.CFNumberSInt32Type, ref vid);
        var pidVal = IOKitInterop.CFNumberCreate(IntPtr.Zero, IOKitInterop.CFNumberSInt32Type, ref pid);
        IOKitInterop.CFDictionarySetValue(dict, vidKey, vidVal);
        IOKitInterop.CFDictionarySetValue(dict, pidKey, pidVal);
        IOKitInterop.CFRelease(vidKey);
        IOKitInterop.CFRelease(pidKey);
        IOKitInterop.CFRelease(vidVal);
        IOKitInterop.CFRelease(pidVal);
        return dict;
    }

    private static void DrainIterator(uint iter)
    {
        if (iter == 0) return;
        uint obj;
        while ((obj = IOKitInterop.IOIteratorNext(iter)) != 0)
        {
            IOKitInterop.IOObjectRelease(obj);
        }
    }

    private void OnAdded(IntPtr refCon, uint iter)
    {
        try
        {
            DrainIterator(iter);
            _logger.LogInformation("USB attach event");
            _changes.OnNext(Unit.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USB added callback threw");
        }
    }

    private void OnRemoved(IntPtr refCon, uint iter)
    {
        try
        {
            DrainIterator(iter);
            _logger.LogInformation("USB detach event");
            _changes.OnNext(Unit.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USB removed callback threw");
        }
    }

    public void Stop()
    {
        Thread? t;
        IntPtr rl;
        lock (_gate)
        {
            t = _runLoopThread;
            rl = _runLoop;
            _runLoopThread = null;
            _runLoop = IntPtr.Zero;
        }
        if (rl != IntPtr.Zero)
        {
            try { IOKitInterop.CFRunLoopStop(rl); } catch { }
        }
        try { t?.Join(TimeSpan.FromSeconds(2)); } catch { }

        if (_addedIter != 0)
        {
            try { IOKitInterop.IOObjectRelease(_addedIter); } catch { }
            _addedIter = 0;
        }
        if (_removedIter != 0)
        {
            try { IOKitInterop.IOObjectRelease(_removedIter); } catch { }
            _removedIter = 0;
        }
        if (_notifyPort != IntPtr.Zero)
        {
            try { IOKitInterop.IONotificationPortDestroy(_notifyPort); } catch { }
            _notifyPort = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _changes.OnCompleted(); } catch { }
        try { _changes.Dispose(); } catch { }
        _ready.Dispose();
    }
}
