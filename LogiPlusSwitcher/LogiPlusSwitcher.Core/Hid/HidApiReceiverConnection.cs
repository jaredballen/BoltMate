using HidApi;
using LogiPlusSwitcher.Core.HidPp;

namespace LogiPlusSwitcher.Core.Hid;

/// <summary>
/// Wraps a libhidapi <see cref="Device"/> with a background read pump that
/// parses inbound bytes into <see cref="HidPpFrame"/> events.
/// </summary>
internal sealed class HidApiReceiverConnection : IReceiverConnection
{
    private readonly Device _device;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public event EventHandler<HidPpFrame>? FrameReceived;
    public event EventHandler<Exception>? ReadError;

    public HidApiReceiverConnection(Device device)
    {
        _device = device;
    }

    public void Write(HidPpFrame frame)
    {
        var bytes = frame.ToBytes();
        lock (_gate)
        {
            _device.Write(bytes);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_pump is { IsCompleted: false })
                return;

            _cts = new CancellationTokenSource();
            _pump = Task.Run(() => Pump(_cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? pump;
        lock (_gate)
        {
            cts = _cts;
            pump = _pump;
            _cts = null;
            _pump = null;
        }

        cts?.Cancel();
        try { pump?.Wait(TimeSpan.FromSeconds(1)); } catch { /* swallow */ }
        cts?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _device.Dispose();
    }

    private void Pump(CancellationToken token)
    {
        Span<byte> buffer = stackalloc byte[HidPpConstants.LongReportLength];
        while (!token.IsCancellationRequested)
        {
            try
            {
                int read;
                lock (_gate)
                {
                    read = _device.ReadTimeout(buffer, milliseconds: 100);
                }

                if (read <= 0)
                    continue;

                var frame = HidPpFrame.TryParse(buffer[..read]);
                if (frame is { } f)
                    FrameReceived?.Invoke(this, f);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                ReadError?.Invoke(this, ex);
                return;
            }
        }
    }
}
