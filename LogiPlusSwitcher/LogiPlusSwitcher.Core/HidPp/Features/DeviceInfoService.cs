using System.Text;

namespace LogiPlusSwitcher.Core.HidPp.Features;

/// <summary>
/// HID++ 2.0 DEVICE_INFO (feature 0x0003). Provides firmware version,
/// transport, and serial-number reads.
/// </summary>
public sealed class DeviceInfoService
{
    private readonly HidPpClient _client;

    public DeviceInfoService(HidPpClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Reads the device's serial number (12 ASCII chars). Returns null if the
    /// device doesn't expose it.
    /// </summary>
    public async Task<string?> GetSerialAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        try
        {
            var reply = await _client.RequestAsync(
                deviceIndex: deviceIndex,
                featureIndex: featureIndex,
                function: 0x2,
                useLongReport: false,
                cancellationToken: ct).ConfigureAwait(false);

            var span = reply.Parameters.Span;
            // The serial occupies the start of the payload until the first NUL
            // or all printable ASCII bytes run out.
            var sb = new StringBuilder();
            foreach (var b in span)
            {
                if (b == 0) break;
                if (b < 0x20 || b > 0x7E) break;
                sb.Append((char)b);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (HidPpException)
        {
            return null;
        }
    }
}
