using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using LogiPlusSwitcher.Hid.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.Hid.IOKit;

/// <summary>
/// IOKit-backed HID++ connection. Reads come from
/// <c>IOHIDDeviceRegisterInputReportCallback</c> firing on a private CFRunLoop
/// thread; writes go via <c>IOHIDDeviceSetReport</c>.
/// </summary>
internal sealed class IOKitReceiverConnection : IReceiverConnection
{
    // Largest report we'll receive from the management interface (long = 20 bytes
    // including report id). Buffer is sized generously to be safe for any future
    // report id we don't recognise.
    private const int InboundBufferSize = 64;

    private readonly IntPtr _device;
    private readonly string _path;
    private readonly ILogger<IOKitReceiverConnection> _logger;
    private readonly Subject<HidPpFrame> _frames = new();
    private readonly Subject<Exception> _readErrors = new();
    private readonly CompositeDisposable _disposables = new();
    private readonly object _gate = new();
    private readonly IntPtr _inboundBuffer;

    private Thread? _runLoopThread;
    private IntPtr _runLoop;
    private IOKitInterop.IOHIDReportCallback? _callback; // kept alive to prevent GC
    private bool _disposed;

    public IObservable<HidPpFrame> InboundFrames => _frames.AsObservable();
    public IObservable<Exception> ReadErrors => _readErrors.AsObservable();

    public IOKitReceiverConnection(IntPtr device, string path, ILogger<IOKitReceiverConnection>? logger = null)
    {
        _device = device;
        _path = path;
        _logger = logger ?? NullLogger<IOKitReceiverConnection>.Instance;
        _inboundBuffer = Marshal.AllocHGlobal(InboundBufferSize);

        _disposables.Add(_frames);
        _disposables.Add(_readErrors);
        _disposables.Add(Disposable.Create(Cleanup));
    }

    public void Write(HidPpFrame frame)
    {
        var bytes = frame.ToBytes();
        if (bytes.Length < 2) throw new InvalidOperationException("Frame too short");

        // IOHIDDeviceSetReport takes the payload EXCLUDING the report id byte,
        // and the report id is passed as a separate parameter.
        var reportId = bytes[0];
        var payloadLength = bytes.Length - 1;
        var ptr = Marshal.AllocHGlobal(payloadLength);
        try
        {
            Marshal.Copy(bytes, 1, ptr, payloadLength);
            lock (_gate)
            {
                var result = IOKitInterop.IOHIDDeviceSetReport(
                    _device,
                    IOKitInterop.ReportTypeOutput,
                    (IntPtr)reportId,
                    ptr,
                    (IntPtr)payloadLength);
                if (result != IOKitInterop.Success)
                    _logger.LogWarning("IOHIDDeviceSetReport returned 0x{Code:X8} (report 0x{Rid:X2}, {Len}B)",
                        result, reportId, payloadLength);
            }
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_runLoopThread is { IsAlive: true }) return;

            // The callback fires on the run loop thread. We hold a strong
            // reference so the GC doesn't collect the delegate.
            _callback = OnInputReport;

            _runLoopThread = new Thread(RunLoopThreadEntry)
            {
                Name = $"IOKit-RunLoop-{_path}",
                IsBackground = true,
            };
            _runLoopThread.Start();
        }
    }

    public void Stop()
    {
        IntPtr rl;
        Thread? t;
        lock (_gate)
        {
            rl = _runLoop;
            t = _runLoopThread;
            _runLoop = IntPtr.Zero;
            _runLoopThread = null;
        }

        if (rl != IntPtr.Zero)
        {
            try { IOKitInterop.CFRunLoopStop(rl); } catch { /* swallow */ }
        }
        try { t?.Join(TimeSpan.FromSeconds(1)); } catch { /* swallow */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposables.Dispose();
    }

    private void RunLoopThreadEntry()
    {
        try
        {
            var rl = IOKitInterop.CFRunLoopGetCurrent();
            lock (_gate) { _runLoop = rl; }

            IOKitInterop.IOHIDDeviceRegisterInputReportCallback(
                _device, _inboundBuffer, (IntPtr)InboundBufferSize, _callback!, IntPtr.Zero);
            IOKitInterop.IOHIDDeviceScheduleWithRunLoop(_device, rl, IOKitInterop.CFRunLoopDefaultMode);

            IOKitInterop.CFRunLoopRun();

            IOKitInterop.IOHIDDeviceUnscheduleFromRunLoop(_device, rl, IOKitInterop.CFRunLoopDefaultMode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IOKit run loop thread crashed");
            _readErrors.OnNext(ex);
        }
    }

    private void OnInputReport(IntPtr context, int result, IntPtr sender,
        uint reportType, uint reportId, IntPtr report, IntPtr reportLength)
    {
        try
        {
            if (result != IOKitInterop.Success)
                return;

            var len = (int)reportLength;
            if (len <= 0 || len > InboundBufferSize)
                return;

            // Reconstruct a full report buffer with the report id prefix so
            // HidPpFrame.TryParse — which expects bytes[0] = reportId — works
            // unchanged.
            var full = new byte[len + 1];
            full[0] = (byte)reportId;
            Marshal.Copy(report, full, 1, len);

            var frame = HidPpFrame.TryParse(full);
            if (frame is { } f)
                _frames.OnNext(f);
        }
        catch (Exception ex)
        {
            _readErrors.OnNext(ex);
        }
    }

    private void Cleanup()
    {
        Stop();

        if (_device != IntPtr.Zero)
        {
            try { IOKitInterop.IOHIDDeviceClose(_device, IOKitInterop.OptionsNone); } catch { /* swallow */ }
        }

        if (_inboundBuffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_inboundBuffer);
    }
}
