using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogiPlusSwitcher.Core;

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

    /// <summary>License key for Pro features (null = free tier).</summary>
    public string? LicenseKey { get; set; }

    /// <summary>
    /// Serial of the receiver designated as "primary" on the Free tier. When
    /// the user has multiple receivers attached and is not Pro, only this
    /// one participates in switch fan-out. Null = no primary chosen yet.
    /// </summary>
    public string? PrimaryReceiverSerial { get; set; }

    /// <summary>
    /// Persisted host-binding cache — keyed by receiver serial, then device
    /// index, then host slot. Lets us populate the topology view immediately
    /// on startup before any device wakes up. Refreshed on every link-up.
    /// </summary>
    public Dictionary<string, Dictionary<byte, Dictionary<byte, PersistedHostBinding>>> CachedHostBindings { get; set; }
        = new();

    /// <summary>Telemetry opt-in flag. Defaults to false; switches to Azure App Insights when true.</summary>
    public bool TelemetryEnabled { get; set; } = false;

    /// <summary>Auto-check for updates on startup + every <see cref="UpdateCheckIntervalHours"/>.</summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>Hours between background update checks. 0 disables periodic; manual only.</summary>
    public int UpdateCheckIntervalHours { get; set; } = 24;

    /// <summary>ISO-8601 timestamp of the last update check, or null if never checked.</summary>
    public string? LastUpdateCheckUtc { get; set; }

    /// <summary>Global hotkey bindings. Default chords ship with the app; users can rebind in Settings.</summary>
    public HotkeySettings Hotkeys { get; set; } = new();

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

    /// <summary>Slots that participate in fan-out. Null = all link-up slots.</summary>
    public byte[]? ParticipatingSlots { get; set; }
}

/// <summary>JSON-serializable mirror of <see cref="Bolt.HostBinding"/> for persistence.</summary>
public sealed class PersistedHostBinding
{
    public bool Paired { get; set; }
    public string? BluetoothAddressHex { get; set; }
    public string? ReceiverName { get; set; }
}

/// <summary>
/// Global hotkey config. Each <see cref="HostBindings"/> entry maps a target
/// host slot (0..2) to a key combo that, when pressed globally, causes us to
/// write <c>0x1814 setCurrentHost(slot)</c> to every paired device on the
/// participating receiver. Decoupled from the receiver-detection paths.
/// </summary>
public sealed class HotkeySettings
{
    /// <summary>Master switch — when false, no hotkeys are registered.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Slot → chord. Default chords picked to dodge common app conflicts.</summary>
    public Dictionary<byte, string> HostBindings { get; set; } = new()
    {
        // macOS will interpret these via the cross-platform serialiser (see
        // HotkeyChord). "Cmd" maps to MOD_WIN on Win and cmdKey on Mac.
        [0] = "Cmd+Ctrl+Shift+1",
        [1] = "Cmd+Ctrl+Shift+2",
        [2] = "Cmd+Ctrl+Shift+3",
    };
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
}

[JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ReceiverSettings))]
[JsonSerializable(typeof(PersistedHostBinding))]
[JsonSerializable(typeof(HotkeySettings))]
[JsonSerializable(typeof(TopologySettings))]
internal partial class AppSettingsContext : JsonSerializerContext { }
