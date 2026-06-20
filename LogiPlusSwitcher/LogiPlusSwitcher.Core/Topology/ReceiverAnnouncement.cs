using System.Text.Json.Serialization;

namespace LogiPlusSwitcher.Core.Topology;

/// <summary>
/// Wire payload broadcast on the LAN every few seconds describing this host's
/// receiver set + currently-online devices. Peers correlate against their own
/// link-state events to detect "device just hopped to that machine".
/// </summary>
public sealed class ReceiverAnnouncement
{
    /// <summary>Schema version. Bump only on breaking changes.</summary>
    public int V { get; set; } = 1;

    /// <summary>Stable UUID identifying the machine. Persisted in AppSettings.</summary>
    public string MachineId { get; set; } = "";

    /// <summary>Human-readable hostname for the topology UI. Best-effort.</summary>
    public string Hostname { get; set; } = "";

    /// <summary>ISO-8601 UTC timestamp the announcement was emitted.</summary>
    public string Timestamp { get; set; } = "";

    /// <summary>One entry per attached Bolt receiver on this host.</summary>
    public List<ReceiverAnnouncementEntry> Receivers { get; set; } = new();
}

public sealed class ReceiverAnnouncementEntry
{
    /// <summary>Receiver serial from BOLT_UNIQUE_ID. Best identifier we have.</summary>
    public string? Serial { get; set; }

    /// <summary>Receiver's own BLE address — lowercase hex, 12 chars. Used to match against device HostBindings.</summary>
    public string? BluetoothAddressHex { get; set; }

    /// <summary>One entry per currently-online paired device.</summary>
    public List<OnlineDeviceEntry> OnlineDevices { get; set; } = new();
}

public sealed class OnlineDeviceEntry
{
    public byte Slot { get; set; }
    /// <summary>WPID as uppercase hex (e.g. "B378").</summary>
    public string? WpidHex { get; set; }
    /// <summary>Friendly name if known.</summary>
    public string? Name { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(ReceiverAnnouncement))]
[JsonSerializable(typeof(ReceiverAnnouncementEntry))]
[JsonSerializable(typeof(OnlineDeviceEntry))]
internal partial class ReceiverAnnouncementContext : JsonSerializerContext { }
