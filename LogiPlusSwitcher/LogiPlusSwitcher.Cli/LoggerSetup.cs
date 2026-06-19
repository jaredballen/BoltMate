using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace LogiPlusSwitcher.Cli;

/// <summary>
/// Builds the <see cref="ILoggerFactory"/> used by the CLI and the Core
/// libraries it constructs.
/// </summary>
/// <remarks>
/// Default sinks:
/// <list type="bullet">
/// <item><description><b>Console</b> — pretty output for interactive runs. Suppressed automatically when not attached to a TTY (so service-mode runs don't waste cycles formatting unread text).</description></item>
/// <item><description><b>Rolling file</b> — per-platform user log dir, 10 MB cap per file, 7 files retained. Use these for "send me your logs" support flows.</description></item>
/// </list>
/// Planned (not yet wired): an Azure Application Insights sink for crash
/// reporting and feature telemetry. Aligns with the Microsoft hosting story
/// for any future paid tier and slots in via Serilog.Sinks.ApplicationInsights
/// without changing any consumer's <see cref="ILogger{T}"/> usage.
/// </remarks>
internal static class LoggerSetup
{
    public static ILoggerFactory Create(LogLevel minimum = LogLevel.Information)
    {
        var logsDir = ResolveLogsDirectory();
        Directory.CreateDirectory(logsDir);
        var logFile = Path.Combine(logsDir, "logiplus-.log");

        var serilog = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilog(minimum))
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        if (Console.IsOutputRedirected is false || Environment.UserInteractive)
        {
            serilog = serilog.WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        Serilog.Log.Logger = serilog.CreateLogger();
        return new SerilogLoggerFactory(Serilog.Log.Logger, dispose: true);
    }

    public static string ResolveLogsDirectory()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Logs", "LogiPlusSwitcher");
        }
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "LogiPlusSwitcher", "Logs");
        }
        // Linux fallback: XDG state dir if set, otherwise ~/.local/state.
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrEmpty(xdg))
            return Path.Combine(xdg, "LogiPlusSwitcher");
        var homeLinux = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeLinux, ".local", "state", "LogiPlusSwitcher");
    }

    private static LogEventLevel ToSerilog(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}
