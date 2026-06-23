using HidApi;
using BoltMate.Hid.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Hid.HidApi;

/// <summary>
/// libhidapi-backed transport. Wires up the macOS non-exclusive flag at
/// construction time so subsequent opens coexist with Logi Options+.
/// </summary>
public sealed class HidApiReceiverTransport : IReceiverTransport
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HidApiReceiverTransport> _logger;
    // On macOS, hid_open / hid_enumerate trigger the Input Monitoring TCC
    // prompt if the per-process cache is Unknown. Defer those calls until
    // the gate flips open so the prompt fires only when the wizard has
    // primed the user. Win/Linux pass a static-true gate.
    private readonly Func<bool> _isInputMonitoringGranted;

    public HidApiReceiverTransport(
        ILoggerFactory? loggerFactory = null,
        Func<bool>? isInputMonitoringGranted = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<HidApiReceiverTransport>();
        _isInputMonitoringGranted = isInputMonitoringGranted ?? (static () => true);
        HidApiBridge.EnsureNativeLibraryResolver();
        // Best-effort on macOS; no-op (returns false) on Windows/Linux.
        var nonExclusive = HidApiBridge.SetMacOsNonExclusive();
        _logger.LogInformation("HID transport initialised; macOS non-exclusive open = {NonExclusive}", nonExclusive);
    }

    public IReadOnlyList<BoltReceiverInfo> Enumerate()
    {
        if (!_isInputMonitoringGranted()) return Array.Empty<BoltReceiverInfo>();
        var infos = global::HidApi.Hid.Enumerate(BoltConstants.LogitechVendorId, BoltConstants.BoltReceiverProductId);
        var result = HidApiDeviceInfoExtensions.ToBoltReceiverInfos(infos).ToList();
        _logger.LogDebug("Enumerated {Count} Bolt receiver management interface(s)", result.Count);
        return result;
    }

    public IReceiverConnection Open(BoltReceiverInfo info)
    {
        if (!_isInputMonitoringGranted())
            throw new InvalidOperationException(
                "Input Monitoring permission not granted — cannot open HID device.");
        _logger.LogInformation("Opening receiver {Product} at {Path}", info.ProductString, info.Path);
        var device = new Device(info.Path);
        return new HidApiReceiverConnection(device, _loggerFactory.CreateLogger<HidApiReceiverConnection>());
    }
}
