using System.Diagnostics;

namespace BoltMate.Core.Topology;

/// <summary>
/// Resolves the set of names by which THIS machine can be identified — every
/// alias a peer device might have stored as a host binding. The Logitech
/// hardware records whatever name was current at pairing time, which on Mac
/// is typically the "Computer Name" friendly form (<c>Jared's M4 MacBook Pro</c>)
/// while .NET's <c>Dns.GetHostName()</c> returns the BSD/DNS short form
/// (<c>Jareds-M4-MBP.allen.family</c>). Matching against only one of those
/// causes cross-machine correlation to silently fail.
/// </summary>
public static class LocalHostIdentity
{
    private static readonly Lazy<IReadOnlyList<string>> _names = new(Resolve);

    /// <summary>All known aliases for this machine, friendly form first.</summary>
    public static IReadOnlyList<string> Names => _names.Value;

    /// <summary>
    /// Preferred broadcast name. Friendly form on macOS (matches what Logi+
    /// stores on devices); <c>MachineName</c> on Windows. Falls back to
    /// <c>Dns.GetHostName()</c> if nothing else resolves.
    /// </summary>
    public static string Canonical => Names.Count > 0 ? Names[0] : "unknown";

    /// <summary>True if <paramref name="candidate"/> matches any local alias.</summary>
    public static bool Matches(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var trimmed = candidate.Trim();
        foreach (var n in Names)
            if (string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static IReadOnlyList<string> Resolve()
    {
        var names = new List<string>();

        if (OperatingSystem.IsMacOS())
        {
            TryAdd(names, ScUtilGet("ComputerName"));
            TryAdd(names, ScUtilGet("LocalHostName"));
        }
        else if (OperatingSystem.IsWindows())
        {
            try { TryAdd(names, Environment.MachineName); } catch { }
        }
        else
        {
            try { TryAdd(names, Environment.MachineName); } catch { }
        }

        try { TryAdd(names, System.Net.Dns.GetHostName()); } catch { }

        return names;
    }

    private static void TryAdd(List<string> names, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        var t = candidate.Trim();
        if (names.Any(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase))) return;
        names.Add(t);
    }

    private static string? ScUtilGet(string key)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/sbin/scutil", $"--get {key}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(2000)) return null;
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }
}
