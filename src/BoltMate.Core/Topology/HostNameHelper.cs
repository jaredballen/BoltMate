namespace BoltMate.Core.Topology;

/// <summary>
/// Case-insensitive exact-string comparison for hostnames. Used wherever two
/// hostnames must match for routing — e.g. a device's HostBindings ReceiverName
/// vs an incoming fan-out target name. <strong>Does not</strong> attempt domain
/// stripping or normalisation — peer-identity matching against the local
/// machine lives in <see cref="LocalHostIdentity.Matches"/> which understands
/// the full alias set per OS.
/// </summary>
public static class HostNameHelper
{
    public static bool HostNameMatches(string? name1, string? name2)
    {
        if (string.IsNullOrWhiteSpace(name1) || string.IsNullOrWhiteSpace(name2))
            return false;
        return string.Equals(name1.Trim(), name2.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
