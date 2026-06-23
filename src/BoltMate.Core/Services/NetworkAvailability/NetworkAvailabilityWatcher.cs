using System.Net.NetworkInformation;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Core.Services;

/// <summary>
/// Wraps <see cref="NetworkChange.NetworkAvailabilityChanged"/> +
/// <see cref="NetworkChange.NetworkAddressChanged"/> behind a single
/// BehaviorSubject. The OS fires these events on enable/disable,
/// cable plug/unplug, Wi-Fi join/leave, and DHCP renew.
/// </summary>
/// <remarks>
/// We re-probe <see cref="NetworkInterface.GetIsNetworkAvailable"/>
/// inside the callback rather than trusting the event's
/// <c>IsAvailable</c> parameter because <c>NetworkAddressChanged</c>
/// has no such parameter and we want a single source of truth.
/// </remarks>
public sealed class NetworkAvailabilityWatcher : INetworkAvailabilityWatcher
{
    private readonly BehaviorSubject<bool> _subject;
    private readonly ILogger<NetworkAvailabilityWatcher> _logger;
    private bool _disposed;

    public NetworkAvailabilityWatcher(ILogger<NetworkAvailabilityWatcher>? logger = null)
    {
        _logger = logger ?? NullLogger<NetworkAvailabilityWatcher>.Instance;
        var initial = SafeGetIsAvailable();
        _subject = new BehaviorSubject<bool>(initial);
        _logger.LogInformation("NetworkAvailabilityWatcher initial state: {Available}", initial);

        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    public bool IsAvailable => _subject.Value;
    public IObservable<bool> IsAvailableChanged => _subject.AsObservable();

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Recompute("availability");
    private void OnAddressChanged(object? sender, EventArgs e) => Recompute("address");

    private void Recompute(string reason)
    {
        if (_disposed) return;
        var next = SafeGetIsAvailable();
        try
        {
            if (next != _subject.Value)
            {
                _logger.LogInformation("Network availability → {Available} ({Reason})", next, reason);
                _subject.OnNext(next);
            }
        }
        catch (ObjectDisposedException) { /* shutdown race */ }
    }

    private static bool SafeGetIsAvailable()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        try { _subject.Dispose(); } catch { /* idempotent */ }
    }
}
