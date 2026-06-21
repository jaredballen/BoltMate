using System.Buffers.Binary;

namespace BoltMate.Core.HidPp.Features;

/// <summary>
/// HID++ 2.0 IRoot (feature 0x0001) — resolves a feature ID into the
/// firmware-assigned index used on subsequent calls.
/// </summary>
public sealed class RootService
{
    private readonly HidPpClient _client;

    public RootService(HidPpClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Asks the device for the index of <paramref name="featureId"/>.
    /// Returns null if the device does not expose that feature (index 0 reply).
    /// </summary>
    public async Task<FeatureLookup?> GetFeatureAsync(byte deviceIndex, ushort featureId, CancellationToken ct = default)
    {
        Span<byte> params_ = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(params_, featureId);

        var reply = await _client.RequestAsync(
            deviceIndex: deviceIndex,
            featureIndex: FeatureIds.IRootIndex,
            function: 0x00,
            parameters: params_.ToArray(),
            useLongReport: false,
            cancellationToken: ct).ConfigureAwait(false);

        var span = reply.Parameters.Span;
        var index = span[0];
        if (index == 0)
            return null;

        var type = span.Length > 1 ? span[1] : (byte)0;
        return new FeatureLookup(featureId, index, type);
    }
}

/// <summary>
/// Result of <see cref="RootService.GetFeatureAsync"/>: the firmware-assigned
/// index for a given feature ID, plus the feature type flags.
/// </summary>
/// <param name="FeatureId">The HID++ 2.0 feature ID requested.</param>
/// <param name="Index">Per-device feature index, used on subsequent calls.</param>
/// <param name="Type">Feature type flags (engineering, manufacturing, etc.).</param>
public sealed record FeatureLookup(ushort FeatureId, byte Index, byte Type)
{
    /// <summary>The feature is marked obsolete and should be ignored if newer alternative exists.</summary>
    public bool IsObsolete => (Type & 0x80) != 0;

    /// <summary>The feature requires "hidden" access (engineering builds).</summary>
    public bool IsHidden => (Type & 0x40) != 0;

    /// <summary>The feature is only available in engineering firmware.</summary>
    public bool IsEngineering => (Type & 0x20) != 0;
}
