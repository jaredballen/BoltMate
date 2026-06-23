using BoltMate.Core.HidPp;

using System.Text;

namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 2.0 DEVICE_FRIENDLY_NAME (feature 0x0007). The user-editable
/// nickname for a device (the thing Logi Options+ lets you rename).
/// Read via fn 0x00 + 0x10, written via fn 0x20, reset via fn 0x30.
/// </summary>
/// <remarks>
/// Not every Bolt device firmware exposes the writable functions; the read
/// path (getFriendlyName) is always safe to try. <see cref="SetFriendlyNameAsync"/>
/// throws <see cref="HidPpException"/> if the firmware refuses, surfacing the
/// HID++ 2.0 error code so the caller can fall back gracefully.
/// </remarks>
public sealed class DeviceFriendlyNameService(IHidPpClient client) : IDeviceFriendlyNameService
{
    private readonly IHidPpClient _client = client;

    /// <summary>Returns the total length of the friendly name (0..50 bytes typical).</summary>
    public async Task<int> GetLengthAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        var reply = await _client.RequestAsync(deviceIndex, featureIndex, 0x0,
            useLongReport: false, cancellationToken: ct).ConfigureAwait(false);
        return reply.Parameters.Span[0];
    }

    /// <summary>Reads the full friendly name. Returns empty string on read failure.</summary>
    public async Task<string> GetAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        var totalLen = await GetLengthAsync(deviceIndex, featureIndex, ct).ConfigureAwait(false);
        if (totalLen <= 0) return string.Empty;

        var sb = new StringBuilder(totalLen);
        byte offset = 0;
        while (offset < totalLen)
        {
            ReadOnlyMemory<byte> request = new byte[] { offset };
            var reply = await _client.RequestAsync(deviceIndex, featureIndex, 0x1,
                parameters: request, useLongReport: false, cancellationToken: ct).ConfigureAwait(false);

            var p = reply.Parameters.Span;
            if (p.Length < 1) break;
            // First byte echoes offset; remaining bytes are name chunk.
            for (var i = 1; i < p.Length && offset < totalLen; i++)
            {
                if (p[i] == 0) goto Done;
                sb.Append((char)p[i]);
                offset++;
            }
        }
        Done:
        return sb.ToString();
    }

    /// <summary>
    /// Writes a new friendly name. Up to 50 ASCII chars. Sends chunks of up to
    /// 15 bytes per fn 0x2 call. Throws <see cref="HidPpException"/> if the
    /// device doesn't support write (most common: <c>Unsupported</c> on
    /// older firmware).
    /// </summary>
    public async Task SetFriendlyNameAsync(byte deviceIndex, byte featureIndex, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var bytes = Encoding.ASCII.GetBytes(name);
        if (bytes.Length > 50)
            bytes = bytes[..50];

        // fn 0x2 setFriendlyName(byteIndex, chunk[..15]). Short reports carry 3 payload bytes,
        // long reports 16. Use long so we can write more per call.
        byte offset = 0;
        while (offset < bytes.Length)
        {
            var chunkLen = Math.Min(15, bytes.Length - offset);
            var payload = new byte[1 + chunkLen];
            payload[0] = offset;
            Array.Copy(bytes, offset, payload, 1, chunkLen);
            await _client.RequestAsync(deviceIndex, featureIndex, 0x2,
                parameters: payload, useLongReport: true, cancellationToken: ct).ConfigureAwait(false);
            offset += (byte)chunkLen;
        }
    }

    /// <summary>
    /// Resets the device's friendly name to its factory default.
    /// </summary>
    public async Task ResetFriendlyNameAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        await _client.RequestAsync(deviceIndex, featureIndex, 0x3,
            useLongReport: false, cancellationToken: ct).ConfigureAwait(false);
    }
}
