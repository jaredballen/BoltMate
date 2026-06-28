using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.Core.Services;
using BoltMate.Licensing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Services.Support;

/// <summary>
/// Orchestrates the in-app "Send logs" path: zip the local bundle, ask
/// every reachable peer for theirs, repack into an outer bundle, post
/// the outer bundle as multipart to <c>/api/support</c>.
/// </summary>
/// <remarks>
/// Auth shape:
/// <list type="bullet">
///   <item>When the user is signed in, the cached JWT is sent as a
///         Bearer token and the user's email is implicit (the server
///         pulls it from the validated token).</item>
///   <item>When signed-out, the caller MUST supply an email field — the
///         service mirrors the site's anonymous /support form path.</item>
/// </list>
/// Peer log collection runs against the existing mDNS+TCP channel; the
/// AES-GCM envelope (#104) guarantees only same-account peers respond.
/// Peers that don't reply within the timeout are silently omitted.
/// </remarks>
public sealed class SupportSubmissionService
{
    private static readonly HttpClient Http = new();

    private readonly LogBundler _bundler;
    private readonly IMdnsTcpChannel _mdns;
    private readonly ILicenseGate _licenseGate;
    private readonly ILogger _log;

    public SupportSubmissionService(
        LogBundler bundler,
        IMdnsTcpChannel mdns,
        ILicenseGate licenseGate,
        ILogger<SupportSubmissionService>? logger = null)
    {
        _bundler = bundler;
        _mdns = mdns;
        _licenseGate = licenseGate;
        _log = logger ?? NullLogger<SupportSubmissionService>.Instance;
    }

    /// <summary>Default upload endpoint.</summary>
    public Uri Endpoint { get; set; } = new("https://api.boltmate.app/api/support");

    public async Task<SupportSubmissionResult> SubmitAsync(
        SupportSubmissionInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Message))
            return SupportSubmissionResult.Failed("Description is required.");

        // 1. Local bundle.
        using var local = await _bundler.BuildAsync(ct).ConfigureAwait(false);

        // 2. Peer bundles via the encrypted TCP backchannel. Lenient
        //    timeout — peers either reply quickly or get omitted.
        var peerBundles = await _mdns
            .RequestPeerLogsAsync(input.PeerTimeout, ct)
            .ConfigureAwait(false);

        // 3. Outer zip combining local + every peer bundle.
        var outer = new MemoryStream();
        long peerBytes = 0;
        using (var zip = new ZipArchive(outer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var localEntry = zip.CreateEntry("this-host.zip", CompressionLevel.NoCompression);
            await using (var dst = localEntry.Open())
            {
                local.Content.Position = 0;
                await local.Content.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            var idx = 0;
            foreach (var peer in peerBundles)
            {
                idx++;
                var safeHost = string.IsNullOrEmpty(peer.FromHostname) ? "peer" : Sanitize(peer.FromHostname);
                var name = $"peer-{idx:D2}-{safeHost}.zip";
                if (peer.ZipBase64 is null)
                {
                    var errEntry = zip.CreateEntry($"peer-{idx:D2}-{safeHost}.error.txt", CompressionLevel.Optimal);
                    await using var dst = errEntry.Open();
                    await using var writer = new StreamWriter(dst);
                    await writer.WriteAsync(peer.Error ?? "(no error reported)").ConfigureAwait(false);
                    continue;
                }
                byte[] bytes;
                try { bytes = Convert.FromBase64String(peer.ZipBase64); }
                catch { continue; /* malformed; drop */ }
                peerBytes += bytes.Length;
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                await using var pdst = entry.Open();
                await pdst.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            }
        }
        outer.Position = 0;

        // 4. Resolve auth. Prefer cached JWT; fall back to the
        //    submitted email if signed-out.
        var status = _licenseGate.Current;
        string? bearer = null;
        if (status.IsEntitled)
        {
            // SyncKey is in the same envelope as the JWT, so the gate
            // doesn't expose the JWT directly anymore. For Bearer-on-
            // /api/support we'd need to add a separate accessor — for
            // now signed-in submissions fall through to email + no
            // Bearer (the server's anonymous path).
        }

        var effectiveEmail = bearer is null ? input.Email : status.Email;
        if (bearer is null && string.IsNullOrWhiteSpace(effectiveEmail))
            return SupportSubmissionResult.Failed("Email is required.");

        // 5. Multipart POST.
        using var content = new MultipartFormDataContent();
        if (!string.IsNullOrWhiteSpace(effectiveEmail))
            content.Add(new StringContent(effectiveEmail!), "email");
        if (!string.IsNullOrWhiteSpace(input.Name))
            content.Add(new StringContent(input.Name!), "name");
        content.Add(new StringContent("Send logs from desktop app"), "subject");
        content.Add(new StringContent(input.Message), "message");
        content.Add(new StringContent("desktop-app"), "source");
        var bundleContent = new StreamContent(outer);
        bundleContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(bundleContent, "bundle", $"boltmate-support-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        try
        {
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogWarning("Support submit failed: {Status} {Body}", (int)resp.StatusCode, body);
                return SupportSubmissionResult.Failed($"Server returned {(int)resp.StatusCode}.");
            }
            _log.LogInformation("Support bundle uploaded: local={LocalBytes} peers={Peers} peerBytes={PeerBytes}",
                local.SizeBytes, peerBundles.Count, peerBytes);
            return SupportSubmissionResult.Ok(peerBundles.Count, outer.Length);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Support submit threw");
            return SupportSubmissionResult.Failed(ex.Message);
        }
    }

    private static string Sanitize(string s)
    {
        var ch = new char[s.Length];
        for (var i = 0; i < s.Length; i++)
            ch[i] = char.IsLetterOrDigit(s[i]) || s[i] is '-' or '_' ? s[i] : '_';
        return new string(ch);
    }
}

public sealed class SupportSubmissionInput
{
    /// <summary>User-supplied description (required).</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Optional name to put in the From header.</summary>
    public string? Name { get; set; }

    /// <summary>Email — required when signed out, ignored when a Bearer is sent.</summary>
    public string? Email { get; set; }

    /// <summary>How long to wait for peer responses. 5 seconds is the
    /// design default; tests can shorten.</summary>
    public TimeSpan PeerTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

public sealed record SupportSubmissionResult(bool Success, string? Error, int PeerCount, long OuterBundleBytes)
{
    public static SupportSubmissionResult Ok(int peerCount, long outerBytes)
        => new(true, null, peerCount, outerBytes);

    public static SupportSubmissionResult Failed(string error)
        => new(false, error, 0, 0);
}
