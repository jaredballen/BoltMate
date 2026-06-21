namespace BoltMate.Core.Topology;

/// <summary>
/// Health snapshot for a single discovery / messaging transport. Each
/// transport has its own independent signal so a user can see which one is
/// blocked without conflating them — the LAN broadcast may be firewalled
/// while Bonjour discovery is fine, or vice versa.
/// </summary>
public sealed record TransportHealth(
    TransportState State,
    string Endpoint,
    string DetailMessage,
    DateTimeOffset LastChangeUtc)
{
    public static TransportHealth Unknown(string endpoint, string message = "no traffic observed yet") =>
        new(TransportState.Unknown, endpoint, message, DateTimeOffset.UtcNow);
}

/// <summary>
/// Three-state tag for transport health. Unknown means we don't yet have
/// enough data to call it healthy or blocked.
/// </summary>
public enum TransportState
{
    Unknown,
    Healthy,
    Blocked,
}
