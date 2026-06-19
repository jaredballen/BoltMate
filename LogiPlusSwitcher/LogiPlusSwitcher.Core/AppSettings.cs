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

    /// <summary>Telemetry opt-in flag. Defaults to false; switches to Azure App Insights when true.</summary>
    public bool TelemetryEnabled { get; set; } = false;

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

[JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ReceiverSettings))]
internal partial class AppSettingsContext : JsonSerializerContext { }
