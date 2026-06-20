using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace BoltMate.App;

/// <summary>
/// Per-OS detection of whether the app is permitted to broadcast on the
/// local network. Used by the Status tab to surface a "Local Network access"
/// indicator and an "Open Privacy/Network Settings" jump.
/// </summary>
public static class NetworkPermission
{
    public enum Status
    {
        Granted,
        Denied,
        Unknown,
    }

    public sealed record Result(Status Status, string Detail);

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);
    private static Result? _cached;
    private static DateTime _cachedAtUtc;

    /// <summary>
    /// Probes the running platform for local-network permission. Result is
    /// cached for 10 seconds to avoid spamming the OS on every UI refresh.
    /// </summary>
    public static Result Check()
    {
        if (_cached is not null && (DateTime.UtcNow - _cachedAtUtc) < CacheDuration)
            return _cached;

        Result result;
        if (OperatingSystem.IsMacOS())
            result = CheckMac();
        else if (OperatingSystem.IsWindows())
            result = CheckWindows();
        else
            result = new Result(Status.Granted, "Local Network access: assumed available");

        _cached = result;
        _cachedAtUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>Force a re-probe on next <see cref="Check"/>.</summary>
    public static void Invalidate()
    {
        _cached = null;
    }

    /// <summary>
    /// Opens the platform's privacy / network settings pane so the user can
    /// grant access. No-op on platforms without a known deep link.
    /// </summary>
    public static void OpenSystemSettings()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork");
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static Result CheckMac()
    {
        // Best signal we have without Apple private API: try to send a 1-byte
        // UDP datagram to the topology multicast group. macOS returns an
        // EHOSTUNREACH (HostUnreachable) or "permission denied" SocketError
        // when Local Network access is denied for the app.
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.MulticastLoopback = true;
            s.SendTo(new byte[] { 0 }, new IPEndPoint(IPAddress.Parse("239.255.41.42"), 41420));
            return new Result(Status.Granted, "Local Network access: granted");
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode == SocketError.HostUnreachable ||
            ex.SocketErrorCode == SocketError.AccessDenied ||
            ex.SocketErrorCode == SocketError.NetworkUnreachable)
        {
            return new Result(Status.Denied, "Local Network access: denied");
        }
        catch
        {
            return new Result(Status.Unknown, "Local Network access: unknown");
        }
    }

    private static Result CheckWindows()
    {
        // Heuristic: locate an "Up" interface that has a default gateway.
        // If we can name one and it's classified Private, broadcast/multicast
        // is allowed. If only a Public interface is present, Windows blocks
        // most discovery traffic by default — surface that to the user.
        try
        {
            var profileName = TryGetActiveProfile();
            if (profileName is null)
                return new Result(Status.Unknown, "Network profile: unknown");

            if (string.Equals(profileName, "Private", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(profileName, "Domain", StringComparison.OrdinalIgnoreCase))
                return new Result(Status.Granted, $"Network: {profileName} (OK)");

            if (string.Equals(profileName, "Public", StringComparison.OrdinalIgnoreCase))
                return new Result(Status.Denied, "Network: Public (discovery blocked)");

            return new Result(Status.Unknown, $"Network: {profileName}");
        }
        catch
        {
            return new Result(Status.Unknown, "Network profile: unknown");
        }
    }

    private static string? TryGetActiveProfile()
    {
        // Try INetworkListManager COM for a clean answer; fall back to a
        // gateway-presence heuristic if COM isn't available.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var profile = QueryNetworkListManagerCategory();
                if (profile is not null) return profile;
            }
            catch
            {
                // fall through to heuristic
            }
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = nic.GetIPProperties();
            if (props.GatewayAddresses.Count > 0)
                return "Unknown";
        }
        return null;
    }

    private static string? QueryNetworkListManagerCategory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var nlmType = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
        if (nlmType is null) return null;
        var nlm = Activator.CreateInstance(nlmType);
        if (nlm is null) return null;
        try
        {
            var enumProp = nlmType.InvokeMember(
                "GetNetworks",
                System.Reflection.BindingFlags.InvokeMethod,
                null, nlm, new object[] { 1 /* NLM_ENUM_NETWORK_CONNECTED */ });
            if (enumProp is not System.Collections.IEnumerable iter) return null;
            foreach (var net in iter)
            {
                if (net is null) continue;
                var t = net.GetType();
                var cat = t.InvokeMember(
                    "GetCategory",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null, net, null);
                if (cat is null) continue;
                return Convert.ToInt32(cat) switch
                {
                    0 => "Public",
                    1 => "Private",
                    2 => "Domain",
                    _ => "Unknown",
                };
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(nlm);
        }
        return null;
    }
}
