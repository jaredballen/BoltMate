using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogiPlusSwitcher.Core.Bolt;

/// <summary>
/// JSON-serializable snapshot of a receiver's full pairing table — receiver
/// metadata plus per-slot device records. Designed to be the input for a
/// future "restore pairings" workflow that calls UnpairAsync + PairAsync to
/// reapply the snapshot. Today, it's a one-way export useful for support
/// flows and for the "wipe + restore" decoupling story.
/// </summary>
public sealed class PairingBackup
{
    public int Version { get; set; } = 1;
    public DateTimeOffset CapturedAt { get; set; }
    public List<ReceiverBackup> Receivers { get; set; } = new();

    public static async Task SaveAsync(PairingBackup backup, string path, CancellationToken ct = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, backup, PairingBackupContext.Default.PairingBackup, ct).ConfigureAwait(false);
    }

    public static async Task<PairingBackup> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return (await JsonSerializer.DeserializeAsync(stream, PairingBackupContext.Default.PairingBackup, ct).ConfigureAwait(false))
               ?? new PairingBackup();
    }
}

public sealed class ReceiverBackup
{
    public string? Serial { get; set; }
    public string? ProductString { get; set; }
    public string? FirmwareVersion { get; set; }
    public List<SlotBackup> Slots { get; set; } = new();
}

public sealed class SlotBackup
{
    public byte DeviceIndex { get; set; }
    public ushort Wpid { get; set; }
    public string? Name { get; set; }
    public string? Serial { get; set; }
    public string? HostIdentifier { get; set; }
    public byte? CurrentHost { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(PairingBackup))]
[JsonSerializable(typeof(ReceiverBackup))]
[JsonSerializable(typeof(SlotBackup))]
internal partial class PairingBackupContext : JsonSerializerContext { }
