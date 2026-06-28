namespace BoltMate.Core.Topology;

/// <summary>
/// Derives a stable per-machine identifier from OS hardware sources.
/// Used by the UDP + mDNS+TCP transports as the BoltMate identity on
/// the LAN. Replaces the previous "random GUID persisted in
/// AppSettings.Topology.MachineId" approach.
/// </summary>
/// <remarks>
/// The returned value is a SHA-256 of the OS-derived hardware ID
/// concatenated with a constant BoltMate-specific salt — same hardware
/// produces the same ID across reboots, but our value can't be used to
/// correlate with anything else that happened to read the same raw OS
/// identifier (other apps, system telemetry).
/// </remarks>
public interface IMachineIdProvider
{
    /// <summary>
    /// Hex-encoded SHA-256 of the salted hardware ID. 64 chars, lower
    /// case. Deterministic for the life of the OS install. Never null
    /// or empty — falls back to a process-lifetime random ID if every
    /// platform probe failed (rare; an explicit warning is logged).
    /// </summary>
    string GetMachineId();
}
