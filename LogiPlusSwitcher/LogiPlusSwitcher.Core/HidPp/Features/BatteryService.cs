namespace LogiPlusSwitcher.Core.HidPp.Features;

/// <summary>
/// HID++ 2.0 UNIFIED_BATTERY (feature 0x1004). Modern Logi devices (MX Master
/// 3/3S, MX Keys series, etc.) expose battery state here.
/// </summary>
public sealed class BatteryService
{
    private readonly HidPpClient _client;

    public BatteryService(HidPpClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Reads battery percentage and charge status. Returns null if the device
    /// doesn't expose UNIFIED_BATTERY or the read fails.
    /// </summary>
    public async Task<BatteryStatus?> GetStatusAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        try
        {
            var reply = await _client.RequestAsync(
                deviceIndex: deviceIndex,
                featureIndex: featureIndex,
                function: 0x1,
                useLongReport: false,
                cancellationToken: ct).ConfigureAwait(false);

            var p = reply.Parameters.Span;
            // UNIFIED_BATTERY fn 0x1 reply layout:
            //   [0] state-of-charge (0-100, or 0xFF if unknown)
            //   [1] level (1=critical, 2=low, 4=good, 8=full ... bitmask)
            //   [2] status (0=discharging, 1=charging, 2=charging-slow, 3=charging-complete, ...)
            //   [3] external-power (0/1)
            var soc = p[0];
            var statusByte = p.Length > 2 ? p[2] : (byte)0;
            var charging = statusByte is 1 or 2 or 3;
            var full = statusByte == 3;
            return new BatteryStatus(
                Percent: soc == 0xFF ? null : soc,
                Charging: charging,
                Full: full);
        }
        catch (HidPpException)
        {
            return null;
        }
    }
}

/// <param name="Percent">State-of-charge 0..100, or null if the device doesn't report a percentage.</param>
/// <param name="Charging">True if the device is connected to external power and charging.</param>
/// <param name="Full">True if charging is complete (battery saturated).</param>
public readonly record struct BatteryStatus(byte? Percent, bool Charging, bool Full)
{
    public override string ToString() =>
        $"{(Percent.HasValue ? $"{Percent}%" : "?")}{(Charging ? " (charging)" : "")}{(Full ? " (full)" : "")}";
}
