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

    public static TransportHealth PermissionDenied(string endpoint, string message = "Local Network permission denied") =>
        new(TransportState.PermissionDenied, endpoint, message, DateTimeOffset.UtcNow);
}

/// <summary>
/// State tag for transport health.
/// <list type="bullet">
///   <item><c>Unknown</c>: no observation yet — undecided.</item>
///   <item><c>Healthy</c>: traffic confirmed.</item>
///   <item><c>Blocked</c>: opened the socket but no expected traffic
///         arrived (firewall ate it, no peers, etc.) — distinguishable
///         from <c>PermissionDenied</c>, which means the socket never
///         opened in the first place.</item>
///   <item><c>PermissionDenied</c>: OS-level grant (Local Network TCC
///         on macOS, Windows Defender firewall) not in place, so the
///         service is parked. UI surfaces a different remediation
///         (open Settings) vs. <c>Blocked</c> (check the network).</item>
/// </list>
/// </summary>
public enum TransportState
{
    Unknown,
    Healthy,
    Blocked,
    PermissionDenied,
}
