using System.Text.Json.Serialization;

namespace BoltMate.Core.Topology;

/// <summary>
/// Wire payload broadcast on the LAN every few seconds describing this host's
/// receiver set + currently-paired devices. Peers filter the message down to
/// devices that mention their own host name in any slot of the slot map, then
/// react to <see cref="LastSwitchEvent"/> (when present) to fan out their own
/// locally-connected devices to the same target host.
/// </summary>
/// <remarks>
/// Protocol version stays at 1 while BoltMate is pre-release; field changes
/// in this iteration aren't a wire bump because there are no other instances
/// in the field.
///
/// Coordinator-only mode (no receiver attached) does NOT broadcast — the
/// broadcast loop bails when <c>ReceiverManager.Receivers.Count == 0</c>.
/// </remarks>
public sealed class ReceiverAnnouncement
{
    /// <summary>Schema version. Bump only on breaking changes after release.</summary>
    public int V { get; set; } = 1;

    /// <summary>Stable UUID identifying the machine. Persisted in AppSettings.</summary>
    public string MachineId { get; set; } = "";

    /// <summary>System host name of the announcing machine. The correlation key.</summary>
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

    /// <summary>
    /// Total Bolt receivers attached to the announcing machine. Convenience
    /// counter — the broadcast still fires when this is 0 so peers can see
    /// "BoltMate is alive on machine X even without a receiver." Used by
    /// the topology UI to show coordinator-only nodes.
    /// </summary>
    public int ReceiverCount { get; set; }

    /// <summary>One entry per attached Bolt receiver on this host. May be empty.</summary>
    public List<ReceiverAnnouncementEntry> Receivers { get; set; } = new();

    /// <summary>
    /// Optional: the most recent host-change event observed on this machine,
    /// carrying the device serial and target host name. Peers use this to
    /// fan out their locally-connected devices to the same target without
    /// having to diff against prior state.
    /// </summary>
    public SwitchEvent? LastSwitchEvent { get; set; }

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
    /// <summary>Receiver serial from BOLT_UNIQUE_ID. Stable identifier across launches.</summary>
    public string? Serial { get; set; }

    /// <summary>One entry per paired device on this receiver. May be empty.</summary>
    public List<DeviceEntry> Devices { get; set; } = new();
}

public sealed class DeviceEntry
{
    /// <summary>Receiver slot (1..6) this device is paired to.</summary>
    public byte Slot { get; set; }

    /// <summary>WPID as uppercase hex (e.g. "B378").</summary>
    public string? WpidHex { get; set; }

    /// <summary>Device serial from feature 0x0003 DEVICE_INFO. Stable physical identity for cross-machine correlation.</summary>
    public string? Serial { get; set; }

    /// <summary>Display name (friendly name, device name, or catalog fallback).</summary>
    public string? Name { get; set; }

    /// <summary>True if the device is currently wirelessly connected to this receiver.</summary>
    public bool LinkUp { get; set; }

    /// <summary>
    /// Which of the device's own host slots it is currently using
    /// (zero-indexed). Read via HID++ 2.0 feature 0x1815 fn 0.
    /// </summary>
    public byte? CurrentHost { get; set; }

    /// <summary>
    /// Per-host-slot map for this device: which host name lives at each slot.
    /// Peers use this for filtering (do any entries reference our host name?)
    /// and for matching by host name during cross-machine fan-out.
    /// </summary>
    public List<DeviceSlotEntry> SlotMap { get; set; } = new();

    /// <summary>Most recently observed battery snapshot, when available.</summary>
    public BatteryEntry? Battery { get; set; }
}

public sealed class DeviceSlotEntry
{
    /// <summary>Zero-indexed host slot on the device (0..2 for most Logi devices).</summary>
    public byte HostIndex { get; set; }

    /// <summary>True if this slot has a paired host.</summary>
    public bool Paired { get; set; }

    /// <summary>
    /// Host name (= OS hostname of the paired computer at pairing time) as
    /// recorded on the device. The correlation key for cross-machine fan-out.
    /// </summary>
    public string? HostName { get; set; }
}

public sealed class BatteryEntry
{
    /// <summary>State-of-charge 0..100, or null if the device doesn't report a percentage.</summary>
    public byte? Percent { get; set; }

    /// <summary>Charge controller state. 0=discharging, 1=charging, 2=slow, 3=complete, 4=error, 5=ext, 6=wireless.</summary>
    public byte State { get; set; }

    /// <summary>True if connected to any external power source.</summary>
    public bool ExternalPower { get; set; }

    /// <summary>Discrete level bucket. 1=critical, 2=low, 4=good, 8=full. Null when device didn't set a bit.</summary>
    public byte? Level { get; set; }
}

public sealed class SwitchEvent
{
    /// <summary>Serial of the device whose host slot just changed.</summary>
    public string? DeviceSerial { get; set; }

    /// <summary>
    /// Host name that the device just switched to — the correlation key
    /// peers use when deciding whether to fan out their own siblings.
    /// </summary>
    public string TargetHostName { get; set; } = "";

    /// <summary>ISO-8601 UTC timestamp the local press / Flow-snoop fired.</summary>
    public string Timestamp { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = false, GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(ReceiverAnnouncement))]
[JsonSerializable(typeof(ReceiverAnnouncementEntry))]
[JsonSerializable(typeof(DeviceEntry))]
[JsonSerializable(typeof(DeviceSlotEntry))]
[JsonSerializable(typeof(BatteryEntry))]
[JsonSerializable(typeof(SwitchEvent))]
[JsonSerializable(typeof(PeerAck))]
internal partial class ReceiverAnnouncementContext : JsonSerializerContext { }
