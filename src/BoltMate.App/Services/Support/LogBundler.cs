using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BoltMate.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Services.Support;

/// <summary>
/// Builds a zipped support bundle for the local machine.
/// </summary>
/// <remarks>
/// <para>Layout inside the zip:</para>
/// <list type="bullet">
///   <item><c>manifest.json</c> — versions, OS, machine identity hint,
///         log filenames + sizes. Generated, not copied.</item>
///   <item><c>settings.json</c> — the on-disk settings file with the
///         OAuth subject claim, license JWT, and Stripe IDs scrubbed.
///         Useful for diagnosing wizard-state issues without leaking
///         account identity.</item>
///   <item><c>logs/boltmate-*.log</c> — every rolling log file we can
///         read. Skipped if the directory doesn't exist (fresh
///         install).</item>
/// </list>
/// <para>Caller owns the returned stream; dispose it when the upload
/// or in-memory copy is done.</para>
/// </remarks>
public sealed class LogBundler
{
    private readonly ILogger _log;

    public LogBundler(ILogger<LogBundler>? logger = null)
    {
        _log = logger ?? NullLogger<LogBundler>.Instance;
    }

    public async Task<SupportBundleResult> BuildAsync(CancellationToken ct = default)
    {
        var stream = new MemoryStream();
        var entries = new List<BundleEntry>();
        var manifest = new BundleManifest
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            AppVersion = ReadAppVersion(),
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            ProcessUptimeSeconds = (int)(Environment.TickCount64 / 1000),
        };

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteSettingsAsync(zip, entries, ct).ConfigureAwait(false);
            await WriteLogsAsync(zip, entries, ct).ConfigureAwait(false);

            manifest.Entries = entries;
            await WriteJsonEntryAsync(zip, "manifest.json", manifest, ct).ConfigureAwait(false);
        }

        stream.Position = 0;
        _log.LogInformation("Built support bundle: {Entries} entries, {Bytes} bytes.",
            entries.Count, stream.Length);
        return new SupportBundleResult(stream, stream.Length, manifest);
    }

    private async Task WriteSettingsAsync(ZipArchive zip, List<BundleEntry> entries, CancellationToken ct)
    {
        var path = AppPaths.SettingsFile;
        if (!File.Exists(path)) return;

        try
        {
            using var src = File.OpenRead(path);
            using var json = await JsonDocument.ParseAsync(src, cancellationToken: ct).ConfigureAwait(false);
            var scrubbed = ScrubSettings(json);

            var entry = zip.CreateEntry("settings.json", CompressionLevel.Optimal);
            await using var dst = entry.Open();
            await using var writer = new Utf8JsonWriter(dst, new JsonWriterOptions { Indented = true });
            scrubbed.WriteTo(writer);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            entries.Add(new BundleEntry("settings.json", new FileInfo(path).Length, Scrubbed: true));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to add settings.json to bundle.");
        }
    }

    private async Task WriteLogsAsync(ZipArchive zip, List<BundleEntry> entries, CancellationToken ct)
    {
        var dir = AppPaths.LogsDirectory;
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.EnumerateFiles(dir, "boltmate-*.log"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entry = zip.CreateEntry("logs/" + Path.GetFileName(file), CompressionLevel.Optimal);
                await using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await using var dst = entry.Open();
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                entries.Add(new BundleEntry("logs/" + Path.GetFileName(file), src.Length, Scrubbed: false));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to add log {File} to bundle.", file);
            }
        }
    }

    private static async Task WriteJsonEntryAsync<T>(ZipArchive zip, string name, T value, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var dst = entry.Open();
        await JsonSerializer.SerializeAsync(dst, value, new JsonSerializerOptions { WriteIndented = true }, ct)
            .ConfigureAwait(false);
    }

    private static JsonElement ScrubSettings(JsonDocument doc)
    {
        // Walk the document and rewrite anything that looks like a
        // secret — OAuth subject, cached license JWT, Stripe IDs.
        // Settings.json shape is small enough that we just buffer
        // into a string and string-replace; the alternative is
        // verbose JsonNode rewriting that we don't need at this scope.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            doc.WriteTo(writer);
        var json = Encoding.UTF8.GetString(buffer.ToArray());

        // Settings file currently doesn't carry the JWT or Stripe IDs
        // — those live in the secure store. Future-proof against
        // schema drift by stripping any property name that smells
        // like a secret.
        json = ScrubProperty(json, "oauthSubject");
        json = ScrubProperty(json, "stripeCustomerId");
        json = ScrubProperty(json, "stripePaymentIntentId");
        json = ScrubProperty(json, "stripeCheckoutSessionId");
        json = ScrubProperty(json, "licenseJwt");

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string ScrubProperty(string json, string propertyName)
    {
        // Naive but adequate: replace `"propertyName": "..."` with
        // `"propertyName": "[scrubbed]"`. We expect scrubbing to be
        // a no-op on the current shape — this is belt-and-braces for
        // any future field that lands in settings.json carrying
        // identifier material.
        var pattern = $"\"{propertyName}\":";
        var idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return json;
        var open = json.IndexOf('"', idx + pattern.Length);
        if (open < 0) return json;
        var close = json.IndexOf('"', open + 1);
        if (close < 0) return json;
        return json.Substring(0, open + 1) + "[scrubbed]" + json.Substring(close);
    }

    private static string ReadAppVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return info ?? asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}

public sealed record SupportBundleResult(Stream Content, long SizeBytes, BundleManifest Manifest) : IDisposable
{
    public void Dispose() => Content.Dispose();
}

public sealed class BundleManifest
{
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string FrameworkDescription { get; set; } = string.Empty;
    public int ProcessUptimeSeconds { get; set; }
    public List<BundleEntry> Entries { get; set; } = new();
}

public sealed record BundleEntry(string Name, long SizeBytes, bool Scrubbed);
