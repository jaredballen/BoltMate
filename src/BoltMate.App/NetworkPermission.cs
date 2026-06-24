using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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

        // Two distinct OS permissions sit behind "network access" on Mac:
        //   1. TCC Local Network — Privacy & Security pane
        //   2. Application Firewall — accept incoming connections (separate)
        // Probe each, then dispatch the right action per sub-permission:
        //   • Granted → no-op
        //   • Denied → open the matching System Settings pane
        //   • Unknown / no-rule → fire a trigger that surfaces the OS prompt
        //
        // Order:
        //   - Start the firewall trigger FIRST (background socket; reactive,
        //     needs time for the OS daemon to evaluate inbound traffic).
        //   - Fire the TCC multicast SEND synchronously after. TCC prompts
        //     immediately; the firewall prompt fires alongside or shortly
        //     after on the same Grant click.
        var ln = CheckMacLocalNetwork();
        var fw = CheckMacFirewall();
        Log.LogInformation("RequestMac: sub-permission state — LocalNetwork={Ln} Firewall={Fw}", ln, fw);

        // Firewall sub-permission
        if (fw == Status.Denied)
        {
            // Existing Block rule — only System Settings can flip it.
            Log.LogInformation("RequestMac: Firewall denies — opening Firewall pane");
            OpenFirewallSettings();
        }
        else if (fw != Status.Granted)
        {
            // No rule yet — bind an inbound socket in the background to
            // surface the firewall dialog while the user is still on the
            // Welcome Network primer.
            StartFirewallTriggerSocket();
        }

        // TCC Local Network sub-permission
        if (ln == Status.Denied)
        {
            // Already declined — TCC won't re-prompt; open Settings.
            Log.LogInformation("RequestMac: TCC Local Network denied — opening Settings");
            OpenSystemSettings();
        }
        else if (ln != Status.Granted)
        {
            // Undecided — multicast SEND triggers the TCC modal.
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.MulticastLoopback = true;
                s.SendTo(new byte[] { 0 }, new IPEndPoint(IPAddress.Parse("239.255.41.42"), 41420));
                Log.LogInformation("RequestMac: multicast send succeeded — TCC Local Network prompt triggered");
            }
            catch (SocketException ex)
            {
                Log.LogWarning(
                    "RequestMac: multicast send failed — SocketError={Code} message={Message}",
                    ex.SocketErrorCode, ex.Message);
            }
        }

        return true;
    }

    private static void OpenFirewallSettings()
    {
        try
        {
            // Direct deep-link to Firewall pane (Network → Firewall).
            Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Firewall");
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Holds a UDP socket bound to the topology port + multicast group in
    /// a background task so macOS Application Firewall has a sustained
    /// inbound bind to evaluate. Runs for ~15s — long enough to overlap
    /// with the user dismissing the TCC dialog and clicking Continue on
    /// the Welcome Network primer. Best-effort; socket-bind failures are
    /// logged and swallowed.
    /// </summary>
    private static void StartFirewallTriggerSocket()
    {
        Task.Run(() =>
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.Bind(new IPEndPoint(IPAddress.Any, 41420));
                s.EnableBroadcast = true;
                s.MulticastLoopback = true;
                var mcast = new MulticastOption(IPAddress.Parse("239.255.41.42"));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, mcast);

                Log.LogInformation("FirewallTrigger: UDP bound on 41420 + joined 239.255.41.42 — holding 15s for firewall dialog");

                // Pump multicast packets steadily so even a quiet LAN
                // delivers inbound traffic to our socket. The firewall
                // daemon needs to see incoming activity, not just a bind.
                var payload = new byte[] { 0 };
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    try { s.SendTo(payload, new IPEndPoint(IPAddress.Parse("239.255.41.42"), 41420)); } catch { }
                    try { s.SendTo(payload, new IPEndPoint(IPAddress.Broadcast, 41420)); } catch { }
                    System.Threading.Thread.Sleep(500);
                }
                Log.LogInformation("FirewallTrigger: socket closed after 15s");
            }
            catch (SocketException ex)
            {
                Log.LogWarning(
                    "FirewallTrigger: bind failed — SocketError={Code} message={Message}",
                    ex.SocketErrorCode, ex.Message);
            }
        });
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
        // Network access on Mac requires BOTH:
        //   1. TCC Local Network (Privacy & Security → Local Network)
        //   2. Application Firewall accept-incoming entry for our binary
        // Both must be granted before peer discovery + UDP topology work
        // end-to-end. Combined IsGranted = AND of both.
        var ln = CheckMacLocalNetwork();
        var fw = CheckMacFirewall();
        Log.LogDebug("CheckMac: LocalNetwork={Ln} Firewall={Fw}", ln, fw);

        if (ln == Status.Granted && fw == Status.Granted)
            return new Result(Status.Granted, "Local Network + Firewall: granted");

        // Surface the most user-actionable denial in the detail string so
        // the Status UI can speak to the right remedy.
        if (ln == Status.Denied)
            return new Result(Status.Denied, "Local Network access: denied — re-enable in System Settings → Privacy");
        if (fw == Status.Denied)
            return new Result(Status.Denied, "Application Firewall: blocking — re-enable in System Settings → Network → Firewall");
        if (fw != Status.Granted)
            return new Result(Status.Denied, "Application Firewall: no rule yet — click Grant to register");
        if (ln != Status.Granted)
            return new Result(Status.Denied, "Local Network access: not yet granted — click Grant to request");

        return new Result(Status.Unknown, "Network access: unknown");
    }

    /// <summary>
    /// Probes TCC Local Network only. Two signals: multicast group join +
    /// broadcast send. Both are TCC-gated on macOS Sequoia+; either failing
    /// with EACCES means Local Network is denied. Multicast SEND (used by
    /// callers as a probe) is unreliable here because the kernel drops at
    /// the discovery layer rather than at syscall — gives false positive.
    /// </summary>
    private static Status CheckMacLocalNetwork()
    {
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
            Log.LogInformation("CheckMacLocalNetwork: multicast-join denied — SocketError={Code}", ex.SocketErrorCode);
            return Status.Denied;
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "CheckMacLocalNetwork: multicast-join unexpected error → Unknown");
            return Status.Unknown;
        }

        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.EnableBroadcast = true;
            s.SendTo(new byte[] { 0 }, new IPEndPoint(IPAddress.Broadcast, 41420));
            return Status.Granted;
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode == SocketError.AccessDenied ||
            ex.SocketErrorCode == SocketError.HostUnreachable)
        {
            Log.LogInformation("CheckMacLocalNetwork: broadcast denied — SocketError={Code}", ex.SocketErrorCode);
            return Status.Denied;
        }
        catch (Exception ex)
        {
            if (joinedMulticast)
            {
                Log.LogDebug(ex, "CheckMacLocalNetwork: broadcast threw post-join; trusting join → Granted");
                return Status.Granted;
            }
            Log.LogWarning(ex, "CheckMacLocalNetwork: broadcast threw with no positive prior signal → Unknown");
            return Status.Unknown;
        }
    }

    /// <summary>
    /// Probes the macOS Application Firewall for a per-app entry covering
    /// our running binary. Shells out to <c>socketfilterfw --listapps</c>
    /// and parses for our binary path + its Allow/Block rule.
    ///   • binary path present + "Allow incoming connections" → Granted
    ///   • binary path present + "Block incoming connections" → Denied
    ///   • binary path NOT present → Unknown (no rule; first bind will prompt)
    /// </summary>
    private static Status CheckMacFirewall()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
                return Status.Unknown;

            var psi = new ProcessStartInfo("/usr/libexec/ApplicationFirewall/socketfilterfw")
            {
                Arguments = "--listapps",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return Status.Unknown;
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(); } catch { }
                return Status.Unknown;
            }
            var output = p.StandardOutput.ReadToEnd();

            // Per-app entries appear as "<index> : <abs path>\n   (rule)\n".
            // Split on lines and walk; once we hit our binary path, the
            // next non-empty line carries the rule.
            var lines = output.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(exe, StringComparison.Ordinal)) continue;
                var rule = i + 1 < lines.Length ? lines[i + 1] : "";
                if (rule.Contains("Allow", StringComparison.OrdinalIgnoreCase))
                    return Status.Granted;
                if (rule.Contains("Block", StringComparison.OrdinalIgnoreCase))
                    return Status.Denied;
                return Status.Unknown;
            }
            // Not in the list → no rule yet. Treat as Unknown so the
            // welcome flow can fire a Request that triggers the OS prompt.
            return Status.Unknown;
        }
        catch (Exception ex)
        {
            Log.LogDebug(ex, "CheckMacFirewall: probe threw");
            return Status.Unknown;
        }
    }

    private static Result CheckWindows()
    {
        // Source of truth on Windows is the firewall rule for our exe, NOT
        // the NLM profile category. Earlier impl returned Granted whenever
        // the profile was Private or Domain — but Windows Defender Firewall
        // still prompts the user on the FIRST inbound bind even on Private,
        // because no rule exists for the exe yet. That meant the welcome
        // wizard auto-advanced past the Network primer, then the Defender
        // prompt fired moments later when topology service bound a UDP
        // listener — exactly the surprise we're trying to avoid.
        //
        // New behavior:
        //   • Allow rule exists  → Granted
        //   • Block rule exists  → Denied (user / IT explicitly denied)
        //   • No rule yet        → Denied so the wizard surfaces the primer
        //                          and the Grant flow can trigger the prompt
        //                          deliberately before topology binds.
        // Profile is captured in the Detail string for diagnostics but is
        // no longer a gate.
        try
        {
            var profileName = TryGetActiveProfile();
            Log.LogDebug("CheckWindows: NLM profile = {Profile}", profileName ?? "unknown");

            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
            {
                Log.LogWarning("CheckWindows: could not resolve own exe path → Unknown");
                return new Result(Status.Unknown, "Network: could not resolve own exe");
            }

            var action = QueryFirewallRuleAction(exe);
            Log.LogInformation(
                "CheckWindows: profile={Profile} exe={Exe} firewall={Action}",
                profileName ?? "unknown", exe, action);

            return action switch
            {
                FirewallAction.Allow => new Result(Status.Granted, $"Network: firewall allow rule present ({profileName ?? "?"})"),
                FirewallAction.Block => new Result(Status.Denied,  $"Network: firewall blocks BoltMate ({profileName ?? "?"})"),
                _                    => new Result(Status.Denied,  $"Network: no firewall rule yet ({profileName ?? "?"})"),
            };
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "CheckWindows threw → Unknown");
            return new Result(Status.Unknown, "Network: probe failed");
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
            if (nic.OperationalStatus is not OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
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
