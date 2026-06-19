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
    /// Reads the friendly name of the requested host. Names are 7-bit ASCII,
    /// returned in 14-byte chunks; this call assembles them into a string.
    /// </summary>
    public async Task<string> GetHostFriendlyNameAsync(byte deviceIndex, byte featureIndex, byte hostIndex, CancellationToken ct = default)
    {
        // fn 0x30 getHostFriendlyName(hostIndex, charIndex) returns a chunk and
        // signals end-of-string via the chunk length and embedded NULs.
        var assembled = new StringBuilder();
        byte charOffset = 0;

        while (charOffset < 64) // safety cap; names rarely exceed 32 chars
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
            // Reply layout: [hostIndex, charIndex, ...up to 14 chars]
            var chunk = p[2..];

            var endReached = false;
            foreach (var c in chunk)
            {
                if (c == 0x00)
                {
                    endReached = true;
                    break;
                }
                assembled.Append((char)c);
                charOffset++;
            }

            if (endReached || chunk.Length == 0)
                break;
        }

        return assembled.ToString();
    }
}

/// <summary>Result of <see cref="HostsInfoService.GetHostsInfoAsync"/>.</summary>
/// <param name="Capability">Capability flag byte; bit 0 set means writable host names.</param>
/// <param name="NumberOfHosts">Total host slots the device supports (typically 3).</param>
/// <param name="CurrentHost">Zero-indexed host currently connected to this receiver.</param>
public readonly record struct HostsInfo(byte Capability, byte NumberOfHosts, byte CurrentHost);
