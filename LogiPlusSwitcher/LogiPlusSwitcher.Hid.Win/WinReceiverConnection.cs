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
/// Native Win32 HID++ connection for the Bolt receiver. Opens BOTH
/// management-interface collections (0xFF00/0x0001 short + 0xFF00/0x0002
/// long) — Windows enumerates them as separate HID interfaces even though
/// they're the same physical USB endpoint. Reads are merged into a single
/// stream; writes are routed by report ID:
/// <list type="bullet">
///   <item>0x10 (short HID++)  → short-collection handle</item>
///   <item>0x11 (long HID++)   → long-collection handle (falls back to short if long is missing)</item>
///   <item>0x20 (DJ legacy)    → short-collection handle</item>
/// </list>
/// </summary>
internal sealed class WinReceiverConnection : IReceiverConnection
{
    private readonly SafeFileHandle _shortHandle;
    private readonly SafeFileHandle? _longHandle;
    private readonly string _path;
    private readonly ILogger<WinReceiverConnection> _logger;
    private readonly Subject<HidPpFrame> _frames = new();
    private readonly Subject<Exception> _readErrors = new();
    private readonly CompositeDisposable _disposables = new();
    private readonly object _gate = new();

    private Thread? _shortReadThread;
    private Thread? _longReadThread;
    private volatile bool _stopping;
    private bool _disposed;

    public IObservable<HidPpFrame> InboundFrames => _frames.AsObservable();
    public IObservable<Exception> ReadErrors => _readErrors.AsObservable();

    public WinReceiverConnection(SafeFileHandle shortHandle, SafeFileHandle? longHandle, string path,
        ILogger<WinReceiverConnection>? logger = null)
    {
        _shortHandle = shortHandle;
        _longHandle = longHandle;
        _path = path;
        _logger = logger ?? NullLogger<WinReceiverConnection>.Instance;
        _disposables.Add(_frames);
        _disposables.Add(_readErrors);
        _disposables.Add(Disposable.Create(Cleanup));
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _stopping = false;
            if (_shortReadThread is null || !_shortReadThread.IsAlive)
            {
                _shortReadThread = new Thread(() => ReadLoop(_shortHandle, 64, "short"))
                {
                    Name = "WinHid-Short", IsBackground = true,
                };
                _shortReadThread.Start();
            }
            if (_longHandle is not null && (_longReadThread is null || !_longReadThread.IsAlive))
            {
                _longReadThread = new Thread(() => ReadLoop(_longHandle, 64, "long"))
                {
                    Name = "WinHid-Long", IsBackground = true,
                };
                _longReadThread.Start();
            }
        }
    }

    public void Stop()
    {
        Thread? t1, t2;
        lock (_gate)
        {
            _stopping = true;
            t1 = _shortReadThread;
            t2 = _longReadThread;
            _shortReadThread = null;
            _longReadThread = null;
        }
        try { NativeMethods.CancelIoEx(_shortHandle, IntPtr.Zero); } catch { }
        if (_longHandle is not null) { try { NativeMethods.CancelIoEx(_longHandle, IntPtr.Zero); } catch { } }
        try { t1?.Join(TimeSpan.FromSeconds(1)); } catch { }
        try { t2?.Join(TimeSpan.FromSeconds(1)); } catch { }
    }

    public void Write(HidPpFrame frame)
    {
        var bytes = frame.ToBytes();
        if (bytes.Length < 1) throw new InvalidOperationException("Frame too short");

        // Pick collection by report ID.
        //  0x10 + 0x20 → short collection
        //  0x11        → long collection (if open), else short
        var reportId = bytes[0];
        var target = reportId == 0x11 && _longHandle is not null ? _longHandle : _shortHandle;

        var buf = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buf, bytes.Length);

            // HidD_SetOutputReport uses the HID class IOCTL path. Per task #31,
            // WriteFile for long reports on the Bolt management interface
            // returns ERROR_INVALID_FUNCTION; the IOCTL path works. Try IOCTL
            // first, fall back to WriteFile on failure.
            var ok = NativeMethods.HidD_SetOutputReport(target, buf, (uint)bytes.Length);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogDebug("HidD_SetOutputReport failed (err 0x{Err:X}, rid 0x{Rid:X2}); falling back to WriteFile",
                    err, reportId);
                WriteFileSync(target, buf, bytes.Length);
                _logger.LogDebug("WinHid OUT (WriteFile, rid 0x{Rid:X2}, {Len}B): {Hex}",
                    reportId, bytes.Length, Convert.ToHexString(bytes));
            }
            else
            {
                _logger.LogDebug("WinHid OUT (HidD,      rid 0x{Rid:X2}, {Len}B): {Hex}",
                    reportId, bytes.Length, Convert.ToHexString(bytes));
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

    private void ReadLoop(SafeFileHandle handle, int bufferSize, string label)
    {
        var buf = Marshal.AllocHGlobal(bufferSize);
        var ovlPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.NATIVE_OVERLAPPED>());
        var ev = NativeMethods.CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, name: null);
        _logger.LogInformation("WinHid read loop started ({Label}, buffer={Buf}B)", label, bufferSize);
        try
        {
            while (!_stopping)
            {
                NativeMethods.ResetEvent(ev);
                var ovl = new NativeMethods.NATIVE_OVERLAPPED { EventHandle = ev };
                Marshal.StructureToPtr(ovl, ovlPtr, false);

                if (!NativeMethods.ReadFile(handle, buf, (uint)bufferSize, IntPtr.Zero, ovlPtr))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == NativeMethods.ERROR_DEVICE_NOT_CONNECTED)
                    {
                        _readErrors.OnNext(new System.ComponentModel.Win32Exception(err, $"{label}: device disconnected"));
                        return;
                    }
                    if (err != NativeMethods.ERROR_IO_PENDING)
                    {
                        _readErrors.OnNext(new System.ComponentModel.Win32Exception(err, $"{label}: ReadFile failed"));
                        Thread.Sleep(100);
                        continue;
                    }
                }

                // Block until the I/O completes OR Stop() cancels it via
                // CancelIoEx. INFINITE wait — the only ways out are completion
                // or cancellation. (Previous code used a 500ms timeout +
                // continue, which leaked overlapped operations by issuing a
                // new ReadFile while the previous was still pending.)
                NativeMethods.WaitForSingleObject(ev, NativeMethods.INFINITE);

                if (!NativeMethods.GetOverlappedResult(handle, ovlPtr, out var bytesRead, wait: false))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == NativeMethods.ERROR_OPERATION_ABORTED)
                    {
                        _logger.LogInformation("WinHid read loop cancelled ({Label})", label);
                        return;
                    }
                    if (err == NativeMethods.ERROR_DEVICE_NOT_CONNECTED)
                    {
                        _readErrors.OnNext(new System.ComponentModel.Win32Exception(err, $"{label}: device disconnected"));
                        return;
                    }
                    _readErrors.OnNext(new System.ComponentModel.Win32Exception(err, $"{label}: GetOverlappedResult failed"));
                    continue;
                }

                if (bytesRead == 0) continue;
                var managed = new byte[bytesRead];
                Marshal.Copy(buf, managed, 0, (int)bytesRead);

                _logger.LogDebug("WinHid IN  ({Label}, {Len}B): {Hex}",
                    label, bytesRead, Convert.ToHexString(managed));

                // Windows' HID class driver can concatenate multiple queued
                // input reports into a single ReadFile result. Split by
                // report-id length and parse each chunk independently.
                SplitAndEmit(managed, label);
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
            try { NativeMethods.CloseHandle(ev); } catch { }
            _logger.LogInformation("WinHid read loop exited ({Label})", label);
        }
    }

    /// <summary>
    /// Splits a (potentially concatenated) HID input buffer into individual
    /// HID++ reports and pushes each onto the inbound frame stream. Report
    /// boundaries are determined by the report id byte at each chunk's
    /// position 0:
    ///   0x10 short HID++   → 7 bytes
    ///   0x11 long HID++    → 20 bytes
    ///   0x20 DJ legacy     → 7 bytes (per HID++ 1.0 / Solaar)
    /// Unknown report ids fall back to "consume rest" so we don't silently
    /// truncate something unexpected.
    /// </summary>
    private void SplitAndEmit(byte[] buf, string label)
    {
        var offset = 0;
        while (offset < buf.Length)
        {
            var rid = buf[offset];
            int len = rid switch
            {
                0x10 or 0x20 => 7,
                0x11         => 20,
                _            => buf.Length - offset, // unknown: consume rest as one
            };
            if (offset + len > buf.Length) len = buf.Length - offset;
            if (len <= 0) break;

            var chunk = new byte[len];
            Array.Copy(buf, offset, chunk, 0, len);
            offset += len;

            var frame = HidPpFrame.TryParse(chunk);
            if (frame is { } f) _frames.OnNext(f);
            else _logger.LogDebug("WinHid IN  ({Label}): unparsable chunk rid=0x{Rid:X2}, dropping", label, rid);
        }
    }

    private void WriteFileSync(SafeFileHandle handle, IntPtr buf, int length)
    {
        var ev = NativeMethods.CreateEventW(IntPtr.Zero, true, false, null);
        var ovlPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.NATIVE_OVERLAPPED>());
        try
        {
            var ovl = new NativeMethods.NATIVE_OVERLAPPED { EventHandle = ev };
            Marshal.StructureToPtr(ovl, ovlPtr, false);
            if (!NativeMethods.WriteFile(handle, buf, (uint)length, IntPtr.Zero, ovlPtr))
            {
                var werr = Marshal.GetLastWin32Error();
                if (werr != NativeMethods.ERROR_IO_PENDING)
                    throw new System.ComponentModel.Win32Exception(werr, "WriteFile failed");
            }
            if (!NativeMethods.GetOverlappedResult(handle, ovlPtr, out _, wait: true))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "WriteFile GetOverlappedResult failed");
        }
        finally
        {
            Marshal.FreeHGlobal(ovlPtr);
            try { NativeMethods.CloseHandle(ev); } catch { }
        }
    }

    private void Cleanup()
    {
        Stop();
        try { _shortHandle.Dispose(); } catch { }
        try { _longHandle?.Dispose(); } catch { }
    }
}
