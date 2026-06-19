namespace LogiPlusSwitcher.Core.Bolt;

/// <summary>
/// Runtime model of a single paired device on a Bolt receiver slot.
/// Mutable — link state and feature-index discovery happen asynchronously
/// after the device is added.
/// </summary>
public sealed class PairedDevice
{
    /// <summary>Receiver slot (1..6).</summary>
    public byte DeviceIndex { get; }

    /// <summary>Wireless product id reported by the receiver in 0x41 notifications.</summary>
    public ushort Wpid { get; set; }

    /// <summary>Friendly name read from BOLT_DEVICE_NAME register, if available.</summary>
    public string? Name { get; set; }

    /// <summary>True if the device is currently wirelessly connected.</summary>
    public bool LinkUp { get; set; }

    /// <summary>Feature index of REPROG_CONTROLS_V4 (0x1B04) or null if unsupported.</summary>
    public byte? ReprogControlsIndex { get; set; }

    /// <summary>Feature index of CHANGE_HOST (0x1814) or null if unsupported.</summary>
    public byte? ChangeHostIndex { get; set; }

    /// <summary>Feature index of HOSTS_INFO (0x1815) or null if unsupported.</summary>
    public byte? HostsInfoIndex { get; set; }

    /// <summary>Feature index of DEVICE_INFO (0x0003).</summary>
    public byte? DeviceInfoIndex { get; set; }

    /// <summary>Feature index of DEVICE_NAME (0x0005).</summary>
    public byte? DeviceNameIndex { get; set; }

    /// <summary>Feature index of UNIFIED_BATTERY (0x1004).</summary>
    public byte? UnifiedBatteryIndex { get; set; }

    /// <summary>Feature index of DEVICE_FRIENDLY_NAME (0x0007) — the writable nickname.</summary>
    public byte? DeviceFriendlyNameIndex { get; set; }

    /// <summary>User-set friendly name from feature 0x0007, when available.</summary>
    public string? FriendlyName { get; set; }

    /// <summary>Most recently observed battery state.</summary>
    public HidPp.Features.BatteryStatus? LastKnownBattery { get; set; }

    /// <summary>CIDs (0x00D1/D2/D3) that this device exposes and we successfully diverted.</summary>
    public IReadOnlyList<ushort> DivertedHostSwitchCids { get; set; } = [];

    /// <summary>Most recently observed current-host index from HOSTS_INFO/CHANGE_HOST reads.</summary>
    public byte? LastKnownCurrentHost { get; set; }

    /// <summary>Device serial as read from <c>BOLT_PAIRING_INFORMATION</c>. Decodes printable ASCII when possible.</summary>
    public string? Serial { get; set; }

    /// <summary>Device BLE address from <c>BOLT_PAIRING_INFORMATION</c> (MSB first).</summary>
    public byte[]? BluetoothAddress { get; set; }

    /// <summary>Wireless protocol version field from <c>BOLT_PAIRING_INFORMATION</c>.</summary>
    public byte? ProtocolVersion { get; set; }

    public PairedDevice(byte deviceIndex)
    {
        if (deviceIndex is < 1 or > 6)
            throw new ArgumentOutOfRangeException(nameof(deviceIndex), deviceIndex, "Bolt receiver supports 6 slots (1..6).");
        DeviceIndex = deviceIndex;
    }

    /// <summary>True if this device can participate in coordinated host switches.</summary>
    public bool CanReceiveHostSwitch => ChangeHostIndex.HasValue;

    /// <summary>True if this device's Easy-Switch keys are being observed.</summary>
    public bool CanEmitHostSwitch => ReprogControlsIndex.HasValue && DivertedHostSwitchCids.Count > 0;

    /// <summary>
    /// The most informative human label for this device. Prefers the
    /// user-set friendly name (feature 0x0007), then the device-side product
    /// name (feature 0x0005), then the BOLT_DEVICE_NAME register read, then
    /// the <see cref="WpidCatalog"/> model lookup, finally a hex placeholder.
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName!
        : !string.IsNullOrWhiteSpace(Name) ? Name!
        : WpidCatalog.LookupOrFallback(Wpid);

    public override string ToString() =>
        $"slot {DeviceIndex} wpid=0x{Wpid:X4} {(LinkUp ? "up" : "down")} name=\"{Name ?? "?"}\" " +
        $"serial={Serial ?? "?"} " +
        $"battery={(LastKnownBattery.HasValue ? LastKnownBattery.Value.ToString() : "?")} " +
        $"feats[1B04={ReprogControlsIndex?.ToString("X2") ?? "-"} " +
        $"1814={ChangeHostIndex?.ToString("X2") ?? "-"} " +
        $"1815={HostsInfoIndex?.ToString("X2") ?? "-"}] " +
        $"diverted=[{string.Join(",", DivertedHostSwitchCids.Select(c => $"0x{c:X4}"))}]";
}
