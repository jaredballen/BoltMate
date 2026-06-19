using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HidApi;
using LogiPlusSwitcher.Core.HidPp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.Core.Hid;

/// <summary>
/// Wraps a libhidapi <see cref="Device"/> with a background read pump that
/// parses inbound bytes into <see cref="HidPpFrame"/>s and pushes them onto
/// the <see cref="InboundFrames"/> stream.
/// </summary>
internal sealed class HidApiReceiverConnection : IReceiverConnection
{
    private readonly Device _device;
    private readonly ILogger<HidApiReceiverConnection> _logger;
    private readonly object _gate = new();
    private readonly Subject<HidPpFrame> _frames = new();
    private readonly Subject<Exception> _readErrors = new();
    private readonly CompositeDisposable _disposables = new();
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public IObservable<HidPpFrame> InboundFrames => _frames.AsObservable();
    public IObservable<Exception> ReadErrors => _readErrors.AsObservable();

    public HidApiReceiverConnection(Device device, ILogger<HidApiReceiverConnection>? logger = null)
    {
        _device = device;
        _logger = logger ?? NullLogger<HidApiReceiverConnection>.Instance;
        _disposables.Add(_frames);
        _disposables.Add(_readErrors);
        _disposables.Add(Disposable.Create(StopAndWait));
        _disposables.Add(Disposable.Create(_device.Dispose));
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

    public void Stop() => StopAndWait();

    public void Dispose() => _disposables.Dispose();

    private void StopAndWait()
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
                    _frames.OnNext(f);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                _logger.LogError(ex, "Read pump failed");
                _readErrors.OnNext(ex);
                return;
            }
        }
    }
}
