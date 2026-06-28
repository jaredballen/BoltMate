using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.Core.Topology;

/// <summary>
/// Default <see cref="IMachineIdProvider"/> implementation. Reads the
/// OS-stable hardware identifier on each platform, salts it, hashes it,
/// and caches the result for the process lifetime.
/// </summary>
/// <remarks>
/// Probe order per platform:
/// <list type="bullet">
///   <item><b>macOS</b>: shells out to <c>ioreg -d2 -c
///     IOPlatformExpertDevice</c> and extracts <c>IOPlatformUUID</c>.</item>
///   <item><b>Windows</b>: reads
///     <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c>.</item>
///   <item><b>Linux</b>: reads <c>/etc/machine-id</c>.</item>
/// </list>
/// Each branch is wrapped — a single probe failure on a developer
/// machine shouldn't crash the app; we log + fall through to a
/// process-lifetime random ID.
/// </remarks>
public sealed class HardwareMachineIdProvider : IMachineIdProvider
{
    // 32-byte fixed salt — keeps our hashed ID uncorrelatable with
    // other apps that happen to read the same raw IOPlatformUUID /
    // MachineGuid / /etc/machine-id value. Not security-sensitive
    // (anyone reading our binary can extract it) — it's a domain
    // separator, not a secret.
    private const string Salt = "boltmate.machine-id.v1";

    private readonly ILogger _logger;
    private readonly Lazy<string> _id;

    public HardwareMachineIdProvider(ILogger<HardwareMachineIdProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<HardwareMachineIdProvider>.Instance;
        _id = new Lazy<string>(Compute);
    }

    public string GetMachineId() => _id.Value;

    private string Compute()
    {
        var raw = TryReadHardwareId();
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Defensive fallback — every supported platform should
            // surface something, but if all three probes fail we'd
            // rather have a per-process random ID than crash. The
            // tradeoff is that the topology MachineId won't survive
            // a restart in that pathological case.
            _logger.LogWarning(
                "All hardware-ID probes failed; falling back to a process-lifetime random ID.");
            raw = Guid.NewGuid().ToString("N");
        }

        var bytes = Encoding.UTF8.GetBytes(Salt + "|" + raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string? TryReadHardwareId()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return ReadMacIOPlatformUUID();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ReadWindowsMachineGuid();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return ReadLinuxMachineId();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hardware-ID probe threw.");
        }
        return null;
    }

    private static string? ReadMacIOPlatformUUID()
    {
        // `ioreg -d2 -c IOPlatformExpertDevice` returns ~30 lines; the
        // line we care about looks like:
        //     "IOPlatformUUID" = "ABCDEF12-3456-..."
        var psi = new ProcessStartInfo("/usr/sbin/ioreg", "-d2 -c IOPlatformExpertDevice")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return null;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(2_000);

        const string marker = "\"IOPlatformUUID\"";
        var idx = stdout.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var lineEnd = stdout.IndexOf('\n', idx);
        if (lineEnd < 0) lineEnd = stdout.Length;
        var line = stdout.Substring(idx, lineEnd - idx);

        var first = line.IndexOf('"', marker.Length);
        if (first < 0) return null;
        var second = line.IndexOf('"', first + 1);
        if (second < 0) return null;
        return line.Substring(first + 1, second - first - 1);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadWindowsMachineGuid()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string;
    }

    private static string? ReadLinuxMachineId()
    {
        const string path = "/etc/machine-id";
        if (!File.Exists(path)) return null;
        var content = File.ReadAllText(path).Trim();
        return string.IsNullOrEmpty(content) ? null : content;
    }
}
