using BoltMate.Core.HidPp;

using System.Text;

namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 2.0 DEVICE_INFO (feature 0x0003). Provides firmware version,
/// transport, and serial-number reads.
/// </summary>
public sealed class DeviceInfoService(IHidPpClient client) : IDeviceInfoService
{
    private readonly IHidPpClient _client = client;

    /// <summary>
    /// Reads firmware info for an entity (0 = main firmware, 1+ = sub-units /
    /// bootloader). Returns null on failure. Format follows Solaar's
    /// <c>get_firmware</c> — entity index 0 is the device's primary firmware.
    /// </summary>
    /// <remarks>
    /// Reply payload (per Logitech HID++ 2.0 spec, feature 0x0003 fn 0x1):
    ///   [0] entityType   (0x00 main firmware, 0x01 bootloader, 0x02 hardware, ...)
    ///   [1] fwPrefix character ('R' for release, 'B' for build, etc.)
    ///   [2..4] fwName ASCII (3 chars)
    ///   [5] versionMajor (BCD)
    ///   [6] versionMinor (BCD)
    ///   [7..8] build number (BCD, big-endian)
    /// </remarks>
    public async Task<DeviceFirmwareInfo?> GetFirmwareAsync(byte deviceIndex, byte featureIndex,
        byte entityIndex = 0, CancellationToken ct = default)
    {
        try
        {
            ReadOnlyMemory<byte> request = new byte[] { entityIndex };
            var reply = await _client.RequestAsync(
                deviceIndex, featureIndex, function: 0x1,
                parameters: request, useLongReport: false,
                cancellationToken: ct).ConfigureAwait(false);

            var p = reply.Parameters.Span;
            if (p.Length < 9) return null;

            var nameBytes = p.Slice(2, 3);
            var name = new StringBuilder();
            foreach (var b in nameBytes)
            {
                if (b < 0x20 || b > 0x7E) break;
                name.Append((char)b);
            }
            return new DeviceFirmwareInfo(
                EntityType: p[0],
                FwPrefix: (char)p[1],
                FwName: name.ToString(),
                VersionMajor: p[5],
                VersionMinor: p[6],
                Build: (ushort)((p[7] << 8) | p[8]));
        }
        catch (HidPpException)
        {
            return null;
        }
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

/// <summary>
/// Firmware identification for a device entity. Format mirrors Solaar's
/// <c>FirmwareInfo</c>. The combined <see cref="DisplayString"/> reads like
/// "RBL 03.05.B0066" — the prefix + name segment lets us tell release/main/
/// bootloader apart at a glance.
/// </summary>
/// <param name="EntityType">0 main firmware, 1 bootloader, 2 hardware, ...</param>
/// <param name="FwPrefix">Single ASCII char prefix ('R' release, 'B' build, ...).</param>
/// <param name="FwName">3-char ASCII firmware family name.</param>
public sealed record DeviceFirmwareInfo(
    byte EntityType, char FwPrefix, string FwName,
    byte VersionMajor, byte VersionMinor, ushort Build)
{
    public string DisplayString =>
        $"{FwPrefix}{FwName} {VersionMajor:X2}.{VersionMinor:X2}.B{Build:X4}";
}
