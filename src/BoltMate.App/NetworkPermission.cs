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
    /// Explicitly ask the OS to surface its local-network permission prompt.
    /// Used by the welcome wizard when the user taps "Grant" — this is the
    /// only path that intentionally fires the OS dialog.
    /// </summary>
    /// <remarks>
    /// macOS: foregrounds the app (TCC will silently deny without prompting
    /// for a background process), then sends a 1-byte UDP datagram to the
    /// topology multicast group. The first such send from a not-yet-decided
    /// app triggers the Local Network TCC dialog. Brief 100ms delay before
    /// the send so AppKit has time to register the foreground transition.
    ///
    /// Windows: open the Network section of Settings so the user can change
    /// their profile to Private (which is what gates discovery on Win). The
    /// firewall prompt itself fires when a listening socket is opened later,
    /// from the topology service. No reliable in-process way to force it.
    ///
    /// Linux: no-op — returns true.
    /// </remarks>
    /// <returns><c>true</c> if the request appeared to be issued without error.</returns>
    public static bool Request()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return RequestMac();
            if (OperatingSystem.IsWindows())
                return RequestWindows();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool RequestMac()
    {
        // CRITICAL: process MUST be foreground when the TCC-gated send fires,
        // or macOS silently denies without surfacing the prompt. Activate the
        // app and surface the Dock icon (the wizard's parent already did this
        // but call again defensively in case Request() is invoked from a
        // background "Fix permissions" path).
        MacActivationPolicy.ShowDockIcon();

        // Give AppKit a beat to flip activation policy before we trip TCC.
        System.Threading.Thread.Sleep(100);

        Invalidate();
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.MulticastLoopback = true;
            s.SendTo(new byte[] { 0 }, new IPEndPoint(IPAddress.Parse("239.255.41.42"), 41420));
            return true;
        }
        catch (SocketException ex)
        {
            // Common cases:
            //   AccessDenied / HostUnreachable: TCC has decided "deny" (no
            //     prompt re-fires for that decision; user must open Settings).
            //   NetworkUnreachable: no live network interface — also no prompt.
            // We log via Trace because we have no ILogger handle in this static.
            Trace.WriteLine($"NetworkPermission.Request: SocketException {ex.SocketErrorCode} ({ex.Message})");
            return false;
        }
    }

    private static bool RequestWindows()
    {
        // Windows has no per-app local-network TCC prompt. The closest
        // analogue is the firewall dialog that fires the FIRST time a process
        // opens a UDP listening socket on a Public profile — that happens
        // later when the topology service starts. Best we can do from the
        // welcome wizard is route the user to the OS Network settings so
        // they can flip to a Private profile if needed.
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
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
        // Multi-probe for reliability. macOS Sequoia behavior:
        //   • A UDP send to multicast SUCCEEDS locally even when TCC denies
        //     Local Network — kernel drops the packet at the discovery layer.
        //     So a send-only probe false-positives Granted on revoked apps.
        //   • Joining a multicast group (IP_ADD_MEMBERSHIP) on 224.0.0.251 is
        //     gated by Local Network TCC and returns EACCES when denied.
        //   • Sending a broadcast to 255.255.255.255 with SO_BROADCAST=1 is
        //     also TCC-gated and returns EACCES.
        // Both signals together give us the cleanest read; we report Denied if
        // EITHER fails with a deny-shaped error. Any unexpected throw is
        // logged via Trace and returned as Unknown.
        bool joinedMulticast = false;
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Any, 0));
            var mcast = new MulticastOption(IPAddress.Parse("224.0.0.251"));
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mcast);
            joinedMulticast = true;
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode == SocketError.AccessDenied ||
            ex.SocketErrorCode == SocketError.HostUnreachable ||
            ex.SocketErrorCode == SocketError.NetworkUnreachable)
        {
            Trace.WriteLine($"NetworkPermission.CheckMac multicast-join denied: {ex.SocketErrorCode}");
            return new Result(Status.Denied, "Local Network access: denied");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NetworkPermission.CheckMac multicast-join error: {ex.Message}");
            return new Result(Status.Unknown, "Local Network access: unknown");
        }

        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.EnableBroadcast = true;
            s.SendTo(new byte[] { 0 }, new IPEndPoint(IPAddress.Broadcast, 41420));
            return new Result(Status.Granted, "Local Network access: granted");
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode == SocketError.AccessDenied ||
            ex.SocketErrorCode == SocketError.HostUnreachable)
        {
            Trace.WriteLine($"NetworkPermission.CheckMac broadcast denied: {ex.SocketErrorCode}");
            return new Result(Status.Denied, "Local Network access: denied");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"NetworkPermission.CheckMac broadcast error: {ex.Message}");
            // If multicast-join succeeded but broadcast threw an unrelated
            // error, assume granted — group-join is the stronger signal.
            return joinedMulticast
                ? new Result(Status.Granted, "Local Network access: granted")
                : new Result(Status.Unknown, "Local Network access: unknown");
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
