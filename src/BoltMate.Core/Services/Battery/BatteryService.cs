using BoltMate.Core.HidPp;

namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 2.0 UNIFIED_BATTERY (feature 0x1004). Modern Logi devices (MX Master
/// 3/3S, MX Keys series, etc.) expose battery state here.
/// </summary>
public sealed class BatteryService(IHidPpClient client) : IBatteryService
{
    private readonly IHidPpClient _client = client;

    /// <summary>
    /// Reads the full battery snapshot — percent, charging state, external
    /// power presence, and the discrete level bucket. Returns null if the
    /// device doesn't expose UNIFIED_BATTERY or the read fails.
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
            return ParsePayload(reply.Parameters.Span);
        }
        catch (HidPpException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the 4-byte UNIFIED_BATTERY payload — the same shape arrives on
    /// the GET_STATUS reply (fn 0x1) and on the BATTERY_STATUS_EVENT
    /// notification (event 0x0). Shared so both paths produce identical
    /// <see cref="BatteryStatus"/> values.
    /// </summary>
    public static BatteryStatus? ParsePayload(ReadOnlySpan<byte> p)
    {
        // UNIFIED_BATTERY payload layout:
        //   [0] state-of-charge (0-100, or 0xFF if unknown)
        //   [1] level bitmask  — 1=critical, 2=low, 4=good, 8=full
        //   [2] charging status — 0=discharging, 1=charging, 2=charging-slow,
        //                          3=charge-complete, 4=charging-error,
        //                          5=discharging-(external), 6=wireless-charging
        //   [3] external power present (0/1) — only present on long-report
        //                                       replies / push events. Short
        //                                       replies cap at 3 param bytes
        //                                       and treat this as 0.
        if (p.Length < 3) return null;
        var soc = p[0];
        var levelByte = p[1];
        var statusByte = p[2];
        var externalPower = p.Length >= 4 && p[3] != 0;

        var state = (ChargingState)(statusByte <= 6 ? statusByte : (byte)0);
        BatteryLevel? level = levelByte switch
        {
            1 => BatteryLevel.Critical,
            2 => BatteryLevel.Low,
            4 => BatteryLevel.Good,
            8 => BatteryLevel.Full,
            _ => null,
        };

        return new BatteryStatus(
            Percent: soc == 0xFF ? null : soc,
            State: state,
            ExternalPower: externalPower,
            Level: level);
    }
}

/// <summary>Charge controller state reported by feature 0x1004.</summary>
public enum ChargingState : byte
{
    Discharging       = 0,
    Charging          = 1,
    ChargingSlow      = 2,
    ChargeComplete    = 3,
    ChargingError     = 4,
    DischargingExternal = 5,
    WirelessCharging  = 6,
}

/// <summary>Discrete battery level bucket from the level bitmask.</summary>
public enum BatteryLevel : byte
{
    Critical = 1,
    Low      = 2,
    Good     = 4,
    Full     = 8,
}

/// <param name="Percent">State-of-charge 0..100, or null if the device doesn't report a percentage.</param>
/// <param name="State">Charge-controller state. <see cref="IsCharging"/> rolls the variants up to a bool.</param>
/// <param name="ExternalPower">True if the device is connected to any external power source.</param>
/// <param name="Level">Discrete level bucket. Null if the device didn't set a recognised bit.</param>
public readonly record struct BatteryStatus(
    byte? Percent,
    ChargingState State,
    bool ExternalPower,
    BatteryLevel? Level)
{
    /// <summary>True if the controller is actively pulling charge in.</summary>
    public bool Charging => State is ChargingState.Charging
                              or ChargingState.ChargingSlow
                              or ChargingState.WirelessCharging;

    /// <summary>True if the battery has reached full charge.</summary>
    public bool Full => State is ChargingState.ChargeComplete || Level is BatteryLevel.Full;

    public override string ToString()
    {
        var pct = Percent.HasValue ? $"{Percent}%" : "?";
        var tag = State switch
        {
            ChargingState.Charging         => " (charging)",
            ChargingState.ChargingSlow     => " (charging slow)",
            ChargingState.ChargeComplete   => " (full)",
            ChargingState.ChargingError    => " (charge error)",
            ChargingState.WirelessCharging => " (wireless charging)",
            _ => string.Empty,
        };
        return $"{pct}{tag}";
    }
}
