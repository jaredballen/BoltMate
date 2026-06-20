using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoltMate.Core;

/// <summary>
/// User-editable JSON config persisted at <see cref="AppPaths.SettingsFile"/>.
/// Mostly empty for v0 — extends as we add UX (named hosts per receiver,
/// per-slot opt-out of fan-out, license key for Pro features).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Schema version of this settings file.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Friendly human names for the three Easy-Switch host slots (global default, per-receiver override below).</summary>
    public string[] HostNames { get; set; } = ["Host 1", "Host 2", "Host 3"];

    /// <summary>Per-receiver overrides keyed by receiver serial.</summary>
    public Dictionary<string, ReceiverSettings> Receivers { get; set; } = new();

    /// <summary>
    /// Persisted host-binding cache — keyed by receiver serial, then device
    /// index, then host slot. Lets us populate the topology view immediately
    /// on startup before any device wakes up. Refreshed on every link-up.
    /// </summary>
    public Dictionary<string, Dictionary<byte, Dictionary<byte, PersistedHostBinding>>> CachedHostBindings { get; set; }
        = new();

    /// <summary>
    /// Cached receiver-level identifier (the 6-byte value read from
    /// RECEIVER_INFO 0x03 — the receiver's per-installation pairing id).
    /// Keyed by receiver HID path. Lets us populate
    /// <see cref="Bolt.BoltReceiver.HostIdentifier"/> immediately on
    /// startup before <see cref="Bolt.BoltReceiver.GetReceiverDetailsAsync"/>
    /// completes — critical for cross-machine topology announcements when a
    /// device first arrives on a host before any other device has enriched
    /// to provide an inference target.
    /// </summary>
    public Dictionary<string, string> CachedReceiverIdentifiers { get; set; } = new();

    /// <summary>
    /// Has the first-run welcome / permission-priming wizard finished? Set to
    /// true by <c>WelcomeWindow</c> when the user clicks "Open BoltMate" on
    /// the final page. We deliberately do NOT cache per-permission grant state
    /// here — the OS is the source of truth and re-checked on every launch
    /// (and periodically while running) by <c>PermissionStatusService</c>.
    /// </summary>
    public bool HasShownWelcome { get; set; } = false;

    /// <summary>Telemetry opt-in flag. Defaults to false; switches to Azure App Insights when true.</summary>
    public bool TelemetryEnabled { get; set; } = false;

    /// <summary>Auto-check for updates on startup + every <see cref="UpdateCheckIntervalHours"/>.</summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>Hours between background update checks. 0 disables periodic; manual only.</summary>
    public int UpdateCheckIntervalHours { get; set; } = 24;

    /// <summary>ISO-8601 timestamp of the last update check, or null if never checked.</summary>
    public string? LastUpdateCheckUtc { get; set; }

    /// <summary>UDP cross-machine topology settings.</summary>
    public TopologySettings Topology { get; set; } = new();

    public static AppSettings Load()
    {
        if (!File.Exists(AppPaths.SettingsFile))
            return new AppSettings();
        try
        {
            using var stream = File.OpenRead(AppPaths.SettingsFile);
            return JsonSerializer.Deserialize(stream, AppSettingsContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        AppPaths.EnsureDirectories();
        using var stream = File.Create(AppPaths.SettingsFile);
        JsonSerializer.Serialize(stream, this, AppSettingsContext.Default.AppSettings);
    }
}

/// <summary>Per-receiver settings, keyed by receiver serial in <see cref="AppSettings.Receivers"/>.</summary>
public sealed class ReceiverSettings
{
    /// <summary>Friendly receiver label.</summary>
    public string? Nickname { get; set; }

    /// <summary>Override host names just for this receiver (null = use global).</summary>
    public string[]? HostNames { get; set; }
}

/// <summary>JSON-serializable mirror of <see cref="Bolt.HostBinding"/> for persistence.</summary>
public sealed class PersistedHostBinding
{
    public bool Paired { get; set; }
    public string? HostIdentifierHex { get; set; }
    public string? ReceiverName { get; set; }
}

/// <summary>
/// Cross-machine UDP topology settings. When enabled, the app broadcasts a
/// periodic announcement of its attached receivers + currently-online device
/// WPIDs on the LAN. Peers that also have this enabled use that signal to
/// fan out remaining local devices when a Bolt device suddenly shows up on
/// a remote machine (e.g. user pressed Easy-Switch on the keyboard).
/// </summary>
public sealed class TopologySettings
{
    /// <summary>Master switch — when false, no broadcast and no listen.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>UDP port for both broadcast and listen. 41420 = arbitrary LAN-only choice.</summary>
    public int Port { get; set; } = 41420;

    /// <summary>Broadcast interval in seconds. 2s gives peers time to correlate without flooding.</summary>
    public int BroadcastIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// How long after a local device link-lost we keep watching for the device
    /// re-appearing on a remote machine. Beyond this, we assume no peer saw it
    /// and skip fan-out.
    /// </summary>
    public int CorrelationWindowSeconds { get; set; } = 3;

    /// <summary>Stable machine id (UUID). Auto-generated on first save.</summary>
    public string? MachineId { get; set; }

    /// <summary>
    /// How many times each announcement is sent back-to-back. Cheap insurance
    /// against single dropped UDP packets — receivers dedup by (machineId, Seq).
    /// </summary>
    public int RepeatCount { get; set; } = 3;

    /// <summary>Gap between repeats in milliseconds.</summary>
    public int RepeatGapMs { get; set; } = 25;

    /// <summary>
    /// When local DJ_PAIRING link-lost fires for any device, we tighten the
    /// broadcast cadence to <see cref="BurstIntervalMs"/> for <see cref="BurstDurationMs"/>
    /// — peers' correlators are actively watching during this window, so we
    /// give them more chances to hear from us.
    /// </summary>
    public int BurstDurationMs { get; set; } = 3000;

    /// <summary>Cadence during a burst window. 200ms is 10× faster than normal.</summary>
    public int BurstIntervalMs { get; set; } = 200;

    /// <summary>
    /// Multicast group joined in addition to LAN broadcast. <c>239.x.x.x</c> is
    /// the admin-scoped IPv4 multicast block — won't leak past the LAN. Some
    /// APs filter broadcast more aggressively than multicast (others the
    /// reverse), so we send both.
    /// </summary>
    public string MulticastGroup { get; set; } = "239.255.41.42";

    /// <summary>Send announcements on multicast in addition to LAN broadcast.</summary>
    public bool UseMulticast { get; set; } = true;

    /// <summary>
    /// Enable a parallel mDNS-discovered TCP transport alongside UDP. mDNS
    /// publishes a <c>_boltmate._udp.local</c> service; the same payload is
    /// also delivered over TCP to every discovered peer. Lets the app keep
    /// working when Wi-Fi APs filter broadcast/multicast.
    /// </summary>
    /// <remarks>
    /// Default false — opt-in. Some Windows configurations (Bonjour service
    /// already running, locked-down dnscache) refuse the mDNS UDP 5353 bind;
    /// the channel survives that but adds startup latency we'd rather avoid
    /// for users on a vanilla machine.
    /// </remarks>
    public bool UseMdnsTcp { get; set; } = false;

    /// <summary>TCP port the mDNS+TCP channel listens on (and advertises via mDNS TXT).</summary>
    public int TcpPort { get; set; } = 41421;

    /// <summary>mDNS service type. Convention: <c>_appname._proto.local</c>.</summary>
    public string MdnsServiceType { get; set; } = "_boltmate._udp.local";
}

[JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ReceiverSettings))]
[JsonSerializable(typeof(PersistedHostBinding))]
[JsonSerializable(typeof(TopologySettings))]
internal partial class AppSettingsContext : JsonSerializerContext { }
