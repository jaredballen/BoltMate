namespace BoltMate.Core.Topology;

/// <summary>
/// Health snapshot for a single discovery / messaging transport. Each
/// transport has its own independent signal so a user can see which one is
/// blocked without conflating them — the LAN broadcast may be firewalled
/// while Bonjour discovery is fine, or vice versa.
/// </summary>
/// <remarks>
/// Detail copy is user-facing — keep it short and ALWAYS pair it with a
/// state that maps to a UI badge color + (where applicable) an action
/// button. Heuristic detail (echo counters, warmup percentages) belongs
/// in logs, not in <see cref="DetailMessage"/>.
/// </remarks>
public sealed record TransportHealth(
    TransportState State,
    string Endpoint,
    string DetailMessage,
    DateTimeOffset LastChangeUtc)
{
    public static TransportHealth Starting(string endpoint, string message = "Starting service") =>
        new(TransportState.Starting, endpoint, message, DateTimeOffset.UtcNow);

    public static TransportHealth Disabled(string endpoint, string message = "Disabled") =>
        new(TransportState.Disabled, endpoint, message, DateTimeOffset.UtcNow);

    public static TransportHealth PermissionDenied(string endpoint, string message = "Local Network permission required") =>
        new(TransportState.PermissionDenied, endpoint, message, DateTimeOffset.UtcNow);

    public static TransportHealth Offline(string endpoint, string message = "No network connection") =>
        new(TransportState.Offline, endpoint, message, DateTimeOffset.UtcNow);
}

/// <summary>
/// State tag for transport health.
/// <list type="bullet">
///   <item><c>Starting</c>: socket up, not enough data yet to declare
///         healthy/blocked. Replaces the old <c>Unknown</c> for the
///         "warming up" case.</item>
///   <item><c>Healthy</c>: traffic confirmed. UI shows the label only —
///         no detail needed.</item>
///   <item><c>Blocked</c>: opened the socket but no expected traffic
///         arrived (firewall ate it, no peers, etc.). Detail copy
///         gives the user one high-level remediation hint.</item>
///   <item><c>PermissionDenied</c>: OS-level grant (Local Network TCC
///         on macOS, Windows Defender firewall) not in place, so the
///         service is parked. Detail names the missing permission so
///         the UI can route a "Grant" button.</item>
///   <item><c>Offline</c>: no usable network interface — NIC disabled,
///         Wi-Fi off, or Ethernet unplugged.</item>
///   <item><c>Disabled</c>: user explicitly turned cross-machine sync
///         off in Settings. Not an alert — informational.</item>
/// </list>
/// </summary>
public enum TransportState
{
    Starting,
    Healthy,
    Blocked,
    PermissionDenied,
    Offline,
    Disabled,
}
