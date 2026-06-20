using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using LogiPlusSwitcher.Hid.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace LogiPlusSwitcher.Hid.Win;

/// <summary>
/// Native-Win32 HID++ connection. Reads come from a dedicated thread doing
/// overlapped <c>ReadFile</c> against the management interface. Writes use
/// <c>HidD_SetOutputReport</c> first (works around the long-report
/// <c>WriteFile</c> failure documented in task #31), falling back to
/// overlapped <c>WriteFile</c> if the HID API path fails.
/// </summary>
internal sealed class WinReceiverConnection : IReceiverConnection
{
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly ushort _inputReportLength;
    private readonly ILogger<WinReceiverConnection> _logger;
    private readonly Subject<HidPpFrame> _frames = new();
    private readonly Subject<Exception> _readErrors = new();
    private readonly CompositeDisposable _disposables = new();
    private readonly object _gate = new();

    private Thread? _readThread;
    private IntPtr _readEvent;
    private volatile bool _stopping;
    private bool _disposed;

    public IObservable<HidPpFrame> InboundFrames => _frames.AsObservable();
    public IObservable<Exception> ReadErrors => _readErrors.AsObservable();

    public WinReceiverConnection(SafeFileHandle handle, string path, ushort inputReportLength,
        ILogger<WinReceiverConnection>? logger = null)
    {
        _handle = handle;
        _path = path;
        _inputReportLength = inputReportLength;
        _logger = logger ?? NullLogger<WinReceiverConnection>.Instance;
        _disposables.Add(_frames);
        _disposables.Add(_readErrors);
        _disposables.Add(Disposable.Create(Cleanup));
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_readThread is { IsAlive: true } || _disposed) return;
            _stopping = false;
            _readEvent = NativeMethods.CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, name: null);
            _readThread = new Thread(ReadLoop)
            {
                Name = $"WinHid-Read-{Path.GetFileName(_path)}",
                IsBackground = true,
            };
            _readThread.Start();
        }
    }

    public void Stop()
    {
        Thread? t;
        IntPtr ev;
        lock (_gate)
        {
            _stopping = true;
            t = _readThread;
            ev = _readEvent;
            _readThread = null;
        }
        if (t is not null)
        {
            // CancelIoEx wakes the read pump from a blocking GetOverlappedResult.
            try { NativeMethods.CancelIoEx(_handle, IntPtr.Zero); } catch { }
            try { t.Join(TimeSpan.FromSeconds(1)); } catch { }
        }
        if (ev != IntPtr.Zero)
        {
            try { NativeMethods.CloseHandle(ev); } catch { }
            lock (_gate) { if (_readEvent == ev) _readEvent = IntPtr.Zero; }
        }
    }

    public void Write(HidPpFrame frame)
    {
        var bytes = frame.ToBytes();
        if (bytes.Length < 1) throw new InvalidOperationException("Frame too short");

        // The HID output report buffer length passed to HidD_SetOutputReport
        // MUST match the device's declared output report size for the given
        // report ID. The Bolt management interface uses two output report
        // sizes: 7 bytes for the short HID++ (report id 0x10) and 20 bytes
        // for the long HID++ (report id 0x11). frame.ToBytes() gives us
        // exactly the right length already (HidPpFrame knows the size from
        // the report id), so just pass it straight through.
        var buf = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buf, bytes.Length);

            // Try HidD_SetOutputReport first — task-#31 history: WriteFile
            // returns ERROR_INVALID_FUNCTION (1) for long reports on the Bolt
            // management interface, but the HID class IOCTL path works.
            var ok = NativeMethods.HidD_SetOutputReport(_handle, buf, (uint)bytes.Length);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogDebug("HidD_SetOutputReport failed (err 0x{Err:X}); falling back to WriteFile", err);

                // Fallback: overlapped WriteFile. Some HID devices accept
                // this path but reject HidD_SetOutputReport. Use a temporary
                // event so we can wait for completion + collect the result.
                var ev = NativeMethods.CreateEventW(IntPtr.Zero, true, false, null);
                try
                {
                    var ovl = new NativeMethods.NATIVE_OVERLAPPED { EventHandle = ev };
                    var ovlPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.NATIVE_OVERLAPPED>());
                    try
                    {
                        Marshal.StructureToPtr(ovl, ovlPtr, false);
                        if (!NativeMethods.WriteFile(_handle, buf, (uint)bytes.Length, IntPtr.Zero, ovlPtr))
                        {
                            var werr = Marshal.GetLastWin32Error();
                            if (werr != NativeMethods.ERROR_IO_PENDING)
                                throw new System.ComponentModel.Win32Exception(werr,
                                    $"WriteFile failed (HidD path also failed with 0x{err:X})");
                        }
                        if (!NativeMethods.GetOverlappedResult(_handle, ovlPtr, out _, wait: true))
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                                "WriteFile GetOverlappedResult failed");
                    }
                    finally { Marshal.FreeHGlobal(ovlPtr); }
                }
                finally { NativeMethods.CloseHandle(ev); }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposables.Dispose();
    }

    // ---------------------------------------------------------------

    private void ReadLoop()
    {
        var buf = Marshal.AllocHGlobal(_inputReportLength);
        var ovlPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.NATIVE_OVERLAPPED>());
        try
        {
            while (!_stopping)
            {
                NativeMethods.ResetEvent(_readEvent);
                var ovl = new NativeMethods.NATIVE_OVERLAPPED { EventHandle = _readEvent };
                Marshal.StructureToPtr(ovl, ovlPtr, false);

                if (!NativeMethods.ReadFile(_handle, buf, _inputReportLength, IntPtr.Zero, ovlPtr))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == NativeMethods.ERROR_DEVICE_NOT_CONNECTED)
                    {
                        _readErrors.OnNext(new System.ComponentModel.Win32Exception(err, "Device disconnected"));
                        return;
                    }
                    if (err != NativeMethods.ERROR_IO_PENDING)
                    {
                        _readErrors.OnNext(new System.ComponentModel.Win32Exception(err, "ReadFile failed"));
                        Thread.Sleep(100);
                        continue;
                    }
                }

                // Wait for either completion or stop signal. CancelIoEx in
                // Stop() will wake us with ERROR_OPERATION_ABORTED.
                var waitRes = NativeMethods.WaitForSingleObject(_readEvent, 500);
                if (waitRes == NativeMethods.WAIT_TIMEOUT)
                {
                    if (_stopping)
                    {
                        try { NativeMethods.CancelIoEx(_handle, ovlPtr); } catch { }
                        return;
                    }
                    continue; // keep waiting on the next loop iter
                }

                if (!NativeMethods.GetOverlappedResult(_handle, ovlPtr, out var bytesRead, wait: false))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == NativeMethods.ERROR_OPERATION_ABORTED) return; // Stop()
                    if (err == NativeMethods.ERROR_DEVICE_NOT_CONNECTED)
                    {
                        _readErrors.OnNext(new System.ComponentModel.Win32Exception(err, "Device disconnected"));
                        return;
                    }
                    _readErrors.OnNext(new System.ComponentModel.Win32Exception(err, "ReadFile GetOverlappedResult failed"));
                    continue;
                }

                if (bytesRead == 0) continue;

                // Copy out + parse on this thread.
                var managed = new byte[bytesRead];
                Marshal.Copy(buf, managed, 0, (int)bytesRead);

                var frame = HidPpFrame.TryParse(managed);
                if (frame is { } f) _frames.OnNext(f);
            }
        }
        catch (Exception ex)
        {
            _readErrors.OnNext(ex);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            Marshal.FreeHGlobal(ovlPtr);
        }
    }

    private void Cleanup()
    {
        Stop();
        try { _handle.Dispose(); } catch { }
    }
}
