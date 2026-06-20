using System.Text;

namespace LogiPlusSwitcher.Core.HidPp.Features;

/// <summary>
/// HID++ 2.0 HOSTS_INFO (feature 0x1815) — discover paired hosts and read
/// their friendly names. Read-only; no host-switch event is emitted on
/// remote changes, so this is purely a poll-based snapshot.
/// </summary>
public sealed class HostsInfoService
{
    private readonly HidPpClient _client;

    public HostsInfoService(HidPpClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Returns capability flags, host count, and the currently-active host
    /// for the device on <paramref name="deviceIndex"/>.
    /// </summary>
    public async Task<HostsInfo> GetHostsInfoAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        var reply = await _client.RequestAsync(
            deviceIndex: deviceIndex,
            featureIndex: featureIndex,
            function: 0x00,
            useLongReport: false,
            cancellationToken: ct).ConfigureAwait(false);

        var p = reply.Parameters.Span;
        return new HostsInfo(
            Capability: p[0],
            NumberOfHosts: p[2],
            CurrentHost: p[3]);
    }

    /// <summary>
    /// Reads metadata for a single host slot — paired status, the receiver's
    /// BLE address as stored on the device, and the host name length. Used to
    /// build the cross-receiver topology map for fan-out routing.
    /// </summary>
    /// <remarks>
    /// Wire layout (per Solaar <c>hidpp20.py:get_host_info</c>): reply payload
    /// starts with <c>[hostIdx, status, busType, bleAddr...]</c>. <c>status</c>
    /// bit 0 set means paired; <c>busType</c> indicates wireless protocol.
    /// </remarks>
    public async Task<Bolt.HostBinding> GetHostInfoAsync(byte deviceIndex, byte featureIndex, byte hostIndex, CancellationToken ct = default)
    {
        ReadOnlyMemory<byte> request = new byte[] { hostIndex };
        var reply = await _client.RequestAsync(
            deviceIndex: deviceIndex,
            featureIndex: featureIndex,
            function: 0x1,
            parameters: request,
            useLongReport: true,
            cancellationToken: ct).ConfigureAwait(false);

        var p = reply.Parameters.Span;
        // Defensive parse — different firmware may omit trailing bytes.
        if (p.Length < 2)
            return new Bolt.HostBinding(hostIndex, Paired: false, HostIdentifier: null, ReceiverName: null);

        var status = p[1];
        var paired = (status & 0x01) != 0 || status == 0x02;

        byte[]? ble = null;
        // BLE address starts after [hostIdx(1), status(1), busType(1)] = offset 3, 6 bytes.
        if (paired && p.Length >= 9)
            ble = p.Slice(3, 6).ToArray();

        return new Bolt.HostBinding(hostIndex, paired, ble, ReceiverName: null);
    }

    /// <summary>
    /// Reads <see cref="GetHostInfoAsync"/> for every host the device exposes
    /// (typically 3) and returns them in slot order.
    /// </summary>
    public async Task<IReadOnlyList<Bolt.HostBinding>> GetAllHostsAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        var info = await GetHostsInfoAsync(deviceIndex, featureIndex, ct).ConfigureAwait(false);
        var bindings = new List<Bolt.HostBinding>(info.NumberOfHosts);
        for (byte h = 0; h < info.NumberOfHosts; h++)
        {
            try
            {
                var binding = await GetHostInfoAsync(deviceIndex, featureIndex, h, ct).ConfigureAwait(false);
                // Best-effort name read — many devices return empty for un-Logi+-named hosts.
                try
                {
                    var name = await GetHostFriendlyNameAsync(deviceIndex, featureIndex, h, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(name))
                        binding = binding with { ReceiverName = name.Trim('\0', ' ') };
                }
                catch (HidPpException) { /* swallow */ }
                bindings.Add(binding);
            }
            catch (HidPpException)
            {
                bindings.Add(new Bolt.HostBinding(h, Paired: false, HostIdentifier: null, ReceiverName: null));
            }
        }
        return bindings;
    }

    /// <summary>
    /// Reads the friendly name of the requested host. Names are 7-bit ASCII,
    /// returned in 14-byte chunks; this call assembles them into a string.
    /// </summary>
    public async Task<string> GetHostFriendlyNameAsync(byte deviceIndex, byte featureIndex, byte hostIndex, CancellationToken ct = default)
    {
        // fn 0x30 getHostFriendlyName(hostIndex, charIndex) returns a chunk and
        // signals end-of-string via the chunk length and embedded NULs.
        // Logi Options+ stores the hostname as UTF-8 (so a macOS hostname like
        // "Jared's M4 MacBook Pro" with a curly apostrophe U+2019 occupies 3
        // bytes E2 80 99). Accumulate raw bytes first, decode at end.
        var bytes = new List<byte>(64);
        byte charOffset = 0;

        while (charOffset < 64) // safety cap; names rarely exceed 32 bytes
        {
            ReadOnlyMemory<byte> request = new byte[] { hostIndex, charOffset };
            var reply = await _client.RequestAsync(
                deviceIndex: deviceIndex,
                featureIndex: featureIndex,
                function: 0x3,
                parameters: request,
                useLongReport: true,
                cancellationToken: ct).ConfigureAwait(false);

            var p = reply.Parameters.Span;
            // Reply layout: [hostIndex, charIndex, ...up to 14 bytes]
            var chunk = p[2..];

            var endReached = false;
            foreach (var c in chunk)
            {
                if (c == 0x00)
                {
                    endReached = true;
                    break;
                }
                bytes.Add(c);
                charOffset++;
            }

            if (endReached || chunk.Length == 0)
                break;
        }

        // Decode as UTF-8. Fall back to Latin-1 (lossless byte→char) if the
        // bytes aren't valid UTF-8 — keeps existing ASCII names working
        // and avoids replacing partial sequences with U+FFFD.
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes.ToArray());
        }
    }
}

/// <summary>Result of <see cref="HostsInfoService.GetHostsInfoAsync"/>.</summary>
/// <param name="Capability">Capability flag byte; bit 0 set means writable host names.</param>
/// <param name="NumberOfHosts">Total host slots the device supports (typically 3).</param>
/// <param name="CurrentHost">Zero-indexed host currently connected to this receiver.</param>
public readonly record struct HostsInfo(byte Capability, byte NumberOfHosts, byte CurrentHost);
