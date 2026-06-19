using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LogiPlusSwitcher.Core;
using Microsoft.Extensions.Logging;

namespace LogiPlusSwitcher.App.Updates;

/// <summary>
/// Update check scaffold. v1 has no backing server — <see cref="CheckAsync"/>
/// always reports "up to date". The shape is here so the UI + scheduler can
/// be wired now and the real cast endpoint (Sparkle/SparkleAppCast on Mac,
/// equivalent JSON manifest on Win) drops in without UI churn later.
/// </summary>
public sealed class UpdateService
{
    private readonly AppSettings _settings;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(AppSettings settings, ILogger<UpdateService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Version string baked into the assembly by Nerdbank.GitVersioning.</summary>
    public string CurrentVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0-unknown";

    public DateTimeOffset? LastCheckUtc =>
        DateTimeOffset.TryParse(_settings.LastUpdateCheckUtc, out var v) ? v : null;

    /// <summary>
    /// Performs an update check. Returns the available update info or null
    /// when already on the latest. Stub: always returns null and stamps the
    /// last-check timestamp.
    /// </summary>
    public Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Update check (stub) — current {Version}", CurrentVersion);
        _settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow.ToString("O");
        _settings.Save();
        return Task.FromResult<UpdateInfo?>(null);
    }
}

public sealed record UpdateInfo(string Version, string DownloadUrl, string? ReleaseNotesUrl);
