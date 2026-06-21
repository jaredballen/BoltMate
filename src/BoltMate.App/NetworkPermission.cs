using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>
    /// Diagnostic logger. Defaults to NullLogger — the App layer sets this
    /// to a real Serilog-backed ILogger at startup so every probe + grant
    /// decision is captured in the on-disk log.
    /// </summary>
    public static ILogger Log { get; set; } = NullLogger.Instance;

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
            Log.LogWarning(
                "RequestMac: multicast send failed — SocketError={Code} message={Message}",
                ex.SocketErrorCode, ex.Message);
            return false;
        }
    }

    private static bool RequestWindows()
    {
        // Windows Defender Firewall fires the "Allow access" prompt the FIRST
        // time an unknown process opens an inbound listening socket on the
        // active profile. Subsequent binds reuse whatever rule the user
        // committed last time — Allow / Block — without re-prompting.
        //
        // Mac-style Grant flow on Windows:
        //   1. If we already have an Allow rule for this exe → no-op
        //   2. If we have a Block rule → try to delete it (per-user rules
        //      are removable without admin; machine-wide are not). Then
        //      fall through to bind so the prompt re-fires.
        //   3. If no rule exists → bind a TcpListener for a few seconds to
        //      surface the OS prompt.
        //   4. If we can't delete a Block rule → open the Windows Defender
        //      Firewall pane so the user can toggle the entry by hand.
        if (!OperatingSystem.IsWindows()) return false;

        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe))
        {
            Log.LogWarning("RequestWindows: could not resolve own exe path; opening Settings");
            OpenSystemSettings();
            return false;
        }

        var action = QueryFirewallRuleAction(exe);
        Log.LogInformation("RequestWindows: pre-action firewall rule for {Exe} = {Action}", exe, action);
        if (action == FirewallAction.Allow) return true;

        if (action == FirewallAction.Block)
        {
            if (!TryRemoveFirewallRulesForExe(exe))
            {
                Log.LogWarning("RequestWindows: could not remove existing Block rule for {Exe} — opening Settings", exe);
                OpenSystemSettings();
                return false;
            }
            Log.LogInformation("RequestWindows: removed existing Block rule(s) for {Exe} — re-binding to re-trigger prompt", exe);
        }

        // Bind an inbound listener. The first bind from an unknown process
        // triggers the prompt. We keep the listener up briefly so the user
        // has time to click Allow — the polling service notices the new
        // rule and auto-advances independently.
        try
        {
            using var listener = new TcpListener(IPAddress.Any, 41420);
            listener.Start();
            Log.LogInformation("RequestWindows: TcpListener bound on 41420 — holding 5s for OS prompt");
            System.Threading.Thread.Sleep(5000);
            listener.Stop();
            return true;
        }
        catch (SocketException ex)
        {
            Log.LogWarning(
                "RequestWindows: listener bind failed — SocketError={Code} message={Message}; opening Settings",
                ex.SocketErrorCode, ex.Message);
            OpenSystemSettings();
            return false;
        }
    }

    private enum FirewallAction { None, Allow, Block }

    private static FirewallAction QueryFirewallRuleAction(string exePath)
    {
        if (!OperatingSystem.IsWindows()) return FirewallAction.None;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return FirewallAction.None;
            var policy = Activator.CreateInstance(type);
            if (policy is null) return FirewallAction.None;
            try
            {
                var rules = type.InvokeMember("Rules",
                    System.Reflection.BindingFlags.GetProperty, null, policy, null);
                if (rules is not System.Collections.IEnumerable iter) return FirewallAction.None;

                int matched = 0, allowed = 0, blocked = 0, disabled = 0, outbound = 0;
                foreach (var rule in iter)
                {
                    if (rule is null) continue;
                    var rt = rule.GetType();
                    var appName = rt.InvokeMember("ApplicationName",
                        System.Reflection.BindingFlags.GetProperty, null, rule, null) as string;
                    if (string.IsNullOrEmpty(appName)) continue;
                    if (!string.Equals(appName, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                    matched++;

                    var dirObj = rt.InvokeMember("Direction",
                        System.Reflection.BindingFlags.GetProperty, null, rule, null);
                    if (Convert.ToInt32(dirObj) != 1 /* NET_FW_RULE_DIR_IN */) { outbound++; continue; }

                    var enabledObj = rt.InvokeMember("Enabled",
                        System.Reflection.BindingFlags.GetProperty, null, rule, null);
                    if (enabledObj is bool b && !b) { disabled++; continue; }

                    var actObj = rt.InvokeMember("Action",
                        System.Reflection.BindingFlags.GetProperty, null, rule, null);
                    var act = Convert.ToInt32(actObj);
                    if (act == 1 /* NET_FW_ACTION_ALLOW */) allowed++;
                    else if (act == 0 /* NET_FW_ACTION_BLOCK */) blocked++;
                }

                Log.LogDebug(
                    "QueryFirewallRuleAction({Exe}): matched={Matched} allow={Allowed} block={Blocked} disabled={Disabled} outbound-only={Outbound}",
                    exePath, matched, allowed, blocked, disabled, outbound);

                if (allowed > 0) return FirewallAction.Allow;
                if (blocked > 0) return FirewallAction.Block;
                return FirewallAction.None;
            }
            finally
            {
                Marshal.FinalReleaseComObject(policy);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "QueryFirewallRuleAction failed for {Exe}", exePath);
            return FirewallAction.None;
        }
    }

    private static bool TryRemoveFirewallRulesForExe(string exePath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return false;
            var policy = Activator.CreateInstance(type);
            if (policy is null) return false;
            try
            {
                var rules = type.InvokeMember("Rules",
                    System.Reflection.BindingFlags.GetProperty, null, policy, null);
                if (rules is null) return false;

                // Collect names first — can't modify collection mid-enumerate.
                var toRemove = new List<string>();
                if (rules is System.Collections.IEnumerable iter)
                {
                    foreach (var rule in iter)
                    {
                        if (rule is null) continue;
                        var rt = rule.GetType();
                        var appName = rt.InvokeMember("ApplicationName",
                            System.Reflection.BindingFlags.GetProperty, null, rule, null) as string;
                        if (string.IsNullOrEmpty(appName)) continue;
                        if (!string.Equals(appName, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                        var name = rt.InvokeMember("Name",
                            System.Reflection.BindingFlags.GetProperty, null, rule, null) as string;
                        if (!string.IsNullOrEmpty(name)) toRemove.Add(name);
                    }
                }

                if (toRemove.Count == 0)
                {
                    Log.LogDebug("TryRemoveFirewallRulesForExe({Exe}): no rules to remove", exePath);
                    return false;
                }

                int removed = 0, failed = 0;
                var rulesType = rules.GetType();
                foreach (var name in toRemove)
                {
                    try
                    {
                        rulesType.InvokeMember("Remove",
                            System.Reflection.BindingFlags.InvokeMethod, null, rules, new object[] { name });
                        removed++;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Log.LogWarning(
                            "TryRemoveFirewallRulesForExe: machine-wide rule \"{Name}\" requires admin to remove ({Message})",
                            name, ex.Message);
                        failed++;
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning(ex, "TryRemoveFirewallRulesForExe: Rules.Remove(\"{Name}\") threw", name);
                        failed++;
                    }
                }
                Log.LogInformation(
                    "TryRemoveFirewallRulesForExe({Exe}): removed={Removed} failed={Failed} of {Total}",
                    exePath, removed, failed, toRemove.Count);
                return failed == 0 && removed > 0;
            }
            finally
            {
                Marshal.FinalReleaseComObject(policy);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "TryRemoveFirewallRulesForExe failed for {Exe}", exePath);
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
                // Allowed apps pane in classic Control Panel — most direct
                // path for the user to toggle our app's network access.
                // ms-settings has no per-app firewall deep-link as of Win 11.
                Process.Start(new ProcessStartInfo("control.exe", "firewall.cpl") { UseShellExecute = true });
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
            Log.LogInformation(
                "CheckMac: multicast-join denied — SocketError={Code} → Denied",
                ex.SocketErrorCode);
            return new Result(Status.Denied, "Local Network access: denied");
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "CheckMac: multicast-join unexpected error → Unknown");
            return new Result(Status.Unknown, "Local Network access: unknown");
        }

        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.EnableBroadcast = true;
            s.SendTo(new byte[] { 0 }, new IPEndPoint(IPAddress.Broadcast, 41420));
            Log.LogDebug("CheckMac: multicast-join + broadcast both succeeded → Granted");
            return new Result(Status.Granted, "Local Network access: granted");
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode == SocketError.AccessDenied ||
            ex.SocketErrorCode == SocketError.HostUnreachable)
        {
            Log.LogInformation(
                "CheckMac: broadcast denied — SocketError={Code} → Denied",
                ex.SocketErrorCode);
            return new Result(Status.Denied, "Local Network access: denied");
        }
        catch (Exception ex)
        {
            // If multicast-join succeeded but broadcast threw an unrelated
            // error, assume granted — group-join is the stronger signal.
            if (joinedMulticast)
            {
                Log.LogDebug(ex, "CheckMac: broadcast threw post-join; trusting join → Granted");
                return new Result(Status.Granted, "Local Network access: granted");
            }
            Log.LogWarning(ex, "CheckMac: broadcast threw with no positive prior signal → Unknown");
            return new Result(Status.Unknown, "Local Network access: unknown");
        }
    }

    private static Result CheckWindows()
    {
        // Two-axis read: NLM profile category AND firewall rule action for
        // our exe. Private/Domain profiles allow discovery by default — no
        // rule needed. Public requires an explicit Allow rule.
        try
        {
            var profileName = TryGetActiveProfile();
            if (profileName is null)
            {
                Log.LogWarning("CheckWindows: NLM returned no active profile → Unknown");
                return new Result(Status.Unknown, "Network profile: unknown");
            }
            Log.LogDebug("CheckWindows: NLM profile = {Profile}", profileName);

            if (string.Equals(profileName, "Private", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(profileName, "Domain", StringComparison.OrdinalIgnoreCase))
                return new Result(Status.Granted, $"Network: {profileName} (allowed by default)");

            if (string.Equals(profileName, "Public", StringComparison.OrdinalIgnoreCase))
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe))
                {
                    Log.LogWarning("CheckWindows: could not resolve own exe path → Unknown");
                    return new Result(Status.Unknown, "Network: Public (could not resolve own exe)");
                }

                var action = QueryFirewallRuleAction(exe);
                Log.LogInformation(
                    "CheckWindows: profile=Public exe={Exe} firewall={Action}",
                    exe, action);
                return action switch
                {
                    FirewallAction.Allow => new Result(Status.Granted, "Network: Public + firewall allowed"),
                    FirewallAction.Block => new Result(Status.Denied,  "Network: Public + firewall blocks"),
                    _                    => new Result(Status.Denied,  "Network: Public (no firewall rule yet)"),
                };
            }

            Log.LogInformation("CheckWindows: unrecognised profile name '{Profile}' → Unknown", profileName);
            return new Result(Status.Unknown, $"Network: {profileName}");
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "CheckWindows threw → Unknown");
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
