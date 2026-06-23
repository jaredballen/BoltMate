namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 2.0 IRoot (feature 0x0001). Resolves a feature ID into the
/// firmware-assigned index used on subsequent calls.
/// </summary>
public interface IRootService
{
    Task<FeatureLookup?> GetFeatureAsync(byte deviceIndex, ushort featureId, CancellationToken ct = default);
}
