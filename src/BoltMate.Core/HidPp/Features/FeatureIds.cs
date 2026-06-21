namespace BoltMate.Core.HidPp.Features;

/// <summary>
/// HID++ 2.0 feature IDs we care about. Indices are firmware-specific; use
/// <see cref="RootService"/> to resolve each ID to its per-device index.
/// </summary>
public static class FeatureIds
{
    /// <summary>IRoot — feature lookup. Index is always 0x00.</summary>
    public const ushort IRoot = 0x0001;

    /// <summary>DEVICE_INFO — firmware version, serial number, transport info.</summary>
    public const ushort DeviceInfo = 0x0003;

    /// <summary>DEVICE_NAME — product name (read-only).</summary>
    public const ushort DeviceName = 0x0005;

    /// <summary>DEVICE_FRIENDLY_NAME — user-editable nickname (writable on firmwares that expose fn 0x2).</summary>
    public const ushort DeviceFriendlyName = 0x0007;

    /// <summary>UNIFIED_BATTERY — modern battery status feature.</summary>
    public const ushort UnifiedBattery = 0x1004;

    /// <summary>BATTERY_VOLTAGE — voltage-based battery status (older devices).</summary>
    public const ushort BatteryVoltage = 0x1001;

    /// <summary>REPROG_CONTROLS_V4 — divert reprogrammable controls (Easy-Switch keys).</summary>
    public const ushort ReprogControlsV4 = 0x1B04;

    /// <summary>CHANGE_HOST — read current host, write to switch host.</summary>
    public const ushort ChangeHost = 0x1814;

    /// <summary>HOSTS_INFO — host capability/name discovery.</summary>
    public const ushort HostsInfo = 0x1815;

    /// <summary>The reserved index of IRoot in every device's feature table.</summary>
    public const byte IRootIndex = 0x00;
}

/// <summary>
/// Easy-Switch control IDs reserved by Logitech (see Solaar special_keys.py).
/// Reported by feature 0x1B04 getCidInfo on devices that expose multi-host.
/// </summary>
public static class EasySwitchCids
{
    public const ushort HostSwitchChannel1 = 0x00D1;
    public const ushort HostSwitchChannel2 = 0x00D2;
    public const ushort HostSwitchChannel3 = 0x00D3;

    /// <summary>Returns true if <paramref name="cid"/> is one of the three Easy-Switch CIDs.</summary>
    public static bool IsHostSwitch(ushort cid) =>
        cid is HostSwitchChannel1 or HostSwitchChannel2 or HostSwitchChannel3;

    /// <summary>Maps a host-switch CID to the zero-indexed target host (0..2).</summary>
    public static int? ToHostIndex(ushort cid) => cid switch
    {
        HostSwitchChannel1 => 0,
        HostSwitchChannel2 => 1,
        HostSwitchChannel3 => 2,
        _ => null
    };
}
