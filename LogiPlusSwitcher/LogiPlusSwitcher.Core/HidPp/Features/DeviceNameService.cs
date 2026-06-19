using System.Text;

namespace LogiPlusSwitcher.Core.HidPp.Features;

/// <summary>
/// HID++ 2.0 DEVICE_NAME (feature 0x0005). Read the device's product name in
/// chunks. Some firmwares also expose a setName function; that path is wired
/// up separately when discovered.
/// </summary>
public sealed class DeviceNameService
{
    private readonly HidPpClient _client;

    public DeviceNameService(HidPpClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Returns the total number of ASCII characters in the device's name.
    /// </summary>
    public async Task<int> GetNameCountAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        var reply = await _client.RequestAsync(
            deviceIndex: deviceIndex,
            featureIndex: featureIndex,
            function: 0x0,
            useLongReport: false,
            cancellationToken: ct).ConfigureAwait(false);
        return reply.Parameters.Span[0];
    }

    /// <summary>
    /// Reads the full device name by concatenating chunks.
    /// </summary>
    public async Task<string> GetNameAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        var totalLength = await GetNameCountAsync(deviceIndex, featureIndex, ct).ConfigureAwait(false);
        if (totalLength <= 0) return string.Empty;

        var sb = new StringBuilder(totalLength);
        byte offset = 0;
        while (offset < totalLength)
        {
            ReadOnlyMemory<byte> request = new byte[] { offset };
            var reply = await _client.RequestAsync(
                deviceIndex: deviceIndex,
                featureIndex: featureIndex,
                function: 0x1,
                parameters: request,
                useLongReport: false,
                cancellationToken: ct).ConfigureAwait(false);

            var span = reply.Parameters.Span;
            // Each chunk is up to 3 bytes on a short reply, or up to 16 on long.
            foreach (var b in span)
            {
                if (b == 0) goto Done;
                sb.Append((char)b);
                if (sb.Length >= totalLength) goto Done;
            }
            offset += (byte)span.Length;
            if (span.Length == 0) break;
        }
        Done:
        return sb.ToString();
    }
}
