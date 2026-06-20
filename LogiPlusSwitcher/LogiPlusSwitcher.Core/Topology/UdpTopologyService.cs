using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using LogiPlusSwitcher.Core.Bolt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.Core.Topology;

/// <summary>
/// LAN-only UDP broadcast + listen. Sends <see cref="ReceiverAnnouncement"/>
/// payloads describing the local <see cref="ReceiverManager"/> state every
/// <see cref="TopologySettings.BroadcastIntervalSeconds"/>, and surfaces
/// inbound announcements from peers on <see cref="Announcements"/>.
/// </summary>
/// <remarks>
/// Network design: bind a single UDP socket to <see cref="IPAddress.Any"/> +
/// the configured port. Send to <see cref="IPAddress.Broadcast"/> on each
/// active broadcast-capable interface. The single bind serves both directions —
/// peers' messages come back via the same socket. Loopback messages (our own
/// machineId) are filtered out before reaching subscribers.
/// </remarks>
public sealed class UdpTopologyService : IDisposable
{
    private readonly ReceiverManager _manager;
    private readonly TopologySettings _settings;
    private readonly string _machineId;
    private readonly string _hostname;
    private readonly ILogger<UdpTopologyService> _logger;
    private readonly Subject<ReceiverAnnouncement> _announcements = new();
    private readonly CompositeDisposable _disposables = new();
    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Hot stream of remote-only announcements (own machineId filtered out).</summary>
    public IObservable<ReceiverAnnouncement> Announcements => _announcements.AsObservable();

    /// <summary>Stable machine id for this host. Echoed in every outgoing announcement.</summary>
    public string MachineId => _machineId;

    public UdpTopologyService(
        ReceiverManager manager,
        TopologySettings settings,
        string machineId,
        ILogger<UdpTopologyService>? logger = null)
    {
        _manager = manager;
        _settings = settings;
        _machineId = machineId;
        _hostname = SafeHostname();
        _logger = logger ?? NullLogger<UdpTopologyService>.Instance;
        _disposables.Add(_announcements);
    }

    /// <summary>Opens the socket, starts the broadcast timer and the receive loop. Idempotent.</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UdpTopologyService));
        if (_socket is not null) return;

        try
        {
            _socket = new UdpClient(AddressFamily.InterNetwork);
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, _settings.Port));
            _socket.EnableBroadcast = true;
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Topology UDP bind on port {Port} failed; topology disabled this session", _settings.Port);
            _socket?.Dispose();
            _socket = null;
            return;
        }

        _cts = new CancellationTokenSource();
        _disposables.Add(Disposable.Create(() =>
        {
            try { _cts?.Cancel(); } catch { }
            try { _socket?.Close(); } catch { }
        }));

        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _ = Task.Run(() => BroadcastLoopAsync(_cts.Token));

        _logger.LogInformation(
            "UDP topology started: port {Port}, machineId {MachineId}, hostname {Hostname}",
            _settings.Port, _machineId, _hostname);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposables.Dispose();
        _socket?.Dispose();
        _cts?.Dispose();
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.BroadcastIntervalSeconds));
        while (!ct.IsCancellationRequested && _socket is not null)
        {
            try
            {
                var payload = BuildAnnouncement();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ReceiverAnnouncementContext.Default.ReceiverAnnouncement);
                foreach (var endpoint in BroadcastEndpoints())
                {
                    try
                    {
                        await _socket.SendAsync(bytes, endpoint, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        // Single endpoint failed — log and continue with the others
                        _logger.LogDebug(ex, "Topology broadcast to {Endpoint} failed", endpoint);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Topology broadcast tick failed");
            }

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _socket is not null)
        {
            UdpReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Topology recv failed; will retry");
                continue;
            }

            try
            {
                var announcement = JsonSerializer.Deserialize(result.Buffer, ReceiverAnnouncementContext.Default.ReceiverAnnouncement);
                if (announcement is null) continue;
                if (announcement.MachineId == _machineId) continue; // our own packet bouncing back
                _announcements.OnNext(announcement);
            }
            catch (JsonException)
            {
                // Foreign UDP traffic on the same port — ignore quietly
            }
        }
    }

    private ReceiverAnnouncement BuildAnnouncement()
    {
        var ann = new ReceiverAnnouncement
        {
            MachineId = _machineId,
            Hostname = _hostname,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
        };
        foreach (var receiver in _manager.Receivers.Items)
        {
            var entry = new ReceiverAnnouncementEntry
            {
                Serial = receiver.Info.Serial,
                BluetoothAddressHex = receiver.BluetoothAddressKey,
            };
            foreach (var device in receiver.Devices.Items)
            {
                if (!device.LinkUp) continue;
                entry.OnlineDevices.Add(new OnlineDeviceEntry
                {
                    Slot = device.DeviceIndex,
                    WpidHex = device.Wpid.ToString("X4"),
                    Name = device.DisplayName,
                });
            }
            ann.Receivers.Add(entry);
        }
        return ann;
    }

    private IEnumerable<IPEndPoint> BroadcastEndpoints()
    {
        var port = _settings.Port;
        var found = new List<IPEndPoint>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (addr.IPv4Mask is null) continue;
                    var broadcast = ComputeBroadcast(addr.Address, addr.IPv4Mask);
                    if (broadcast is not null) found.Add(new IPEndPoint(broadcast, port));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Topology interface enumeration failed; falling back to 255.255.255.255");
        }

        if (found.Count == 0)
            found.Add(new IPEndPoint(IPAddress.Broadcast, port));
        return found;
    }

    private static IPAddress? ComputeBroadcast(IPAddress addr, IPAddress mask)
    {
        var a = addr.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4) return null;
        var b = new byte[4];
        for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | (~m[i] & 0xFF));
        return new IPAddress(b);
    }

    private static string SafeHostname()
    {
        try { return Dns.GetHostName(); }
        catch { return "unknown"; }
    }
}
