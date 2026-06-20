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

    /// <summary>
    /// Monotonically increasing per-machine sequence number. Used by the
    /// receiver-side dedup to suppress N× repeats of the same announcement
    /// (we send each announcement multiple times in case any single UDP
    /// packet is dropped).
    /// </summary>
    public ulong Seq { get; set; }

    /// <summary>One entry per attached Bolt receiver on this host.</summary>
    public List<ReceiverAnnouncementEntry> Receivers { get; set; } = new();

    /// <summary>
    /// Mutual acks: "the last Seq I've received from machineId K is V".
    /// Receivers use this to measure their own outbound packet loss — if peer
    /// echoes V but we're already at V+N, then N of our announcements never
    /// reached them.
    /// </summary>
    public List<PeerAck> Acks { get; set; } = new();
}

public sealed class PeerAck
{
    public string MachineId { get; set; } = "";
    public ulong LastSeq { get; set; }
}

public sealed class ReceiverAnnouncementEntry
{
    /// <summary>Receiver serial from BOLT_UNIQUE_ID. Best identifier we have.</summary>
    public string? Serial { get; set; }

    /// <summary>Receiver's own BLE address — lowercase hex, 12 chars. Used to match against device HostBindings.</summary>
    public string? HostIdentifierHex { get; set; }

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

    /// <summary>
    /// Which of the device's own host slots it is currently using
    /// (zero-indexed). Read via HID++ 2.0 feature 0x1815 fn 0.
    /// </summary>
    public byte? CurrentHost { get; set; }

    /// <summary>
    /// The device's per-host-slot pairing identifiers (read via feature
    /// 0x1815 fn 1). The peer correlator uses these — specifically the
    /// entry whose <see cref="DeviceHostBindingEntry.HostIndex"/> matches
    /// <see cref="CurrentHost"/> — to match against local siblings'
    /// HostBindings, no receiver-level identifier required.
    /// </summary>
    public List<DeviceHostBindingEntry> HostBindings { get; set; } = new();
}

public sealed class DeviceHostBindingEntry
{
    public byte HostIndex { get; set; }
    public bool Paired { get; set; }
    /// <summary>The 6-byte per-pairing identifier, lowercase hex. Null if unpaired.</summary>
    public string? IdentifierHex { get; set; }
    /// <summary>Friendly host name as stored on the device (Logi+ assigns), if known.</summary>
    public string? ReceiverName { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(ReceiverAnnouncement))]
[JsonSerializable(typeof(ReceiverAnnouncementEntry))]
[JsonSerializable(typeof(OnlineDeviceEntry))]
[JsonSerializable(typeof(DeviceHostBindingEntry))]
[JsonSerializable(typeof(PeerAck))]
internal partial class ReceiverAnnouncementContext : JsonSerializerContext { }
