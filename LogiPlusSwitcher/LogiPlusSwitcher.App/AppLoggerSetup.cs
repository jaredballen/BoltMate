using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace LogiPlusSwitcher.App;

/// <summary>
/// Mirror of the CLI's <c>LoggerSetup</c> for the Avalonia app process. The
/// log file path is identical to the CLI's so support flows aggregate both.
/// </summary>
internal static class AppLoggerSetup
{
    public static ILoggerFactory Create(LogLevel minimum = LogLevel.Information)
    {
        var logsDir = ResolveLogsDirectory();
        Directory.CreateDirectory(logsDir);
        var logFile = Path.Combine(logsDir, "logiplus-app-.log");

        var serilog = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilog(minimum))
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Serilog.Log.Logger = serilog;
        return new SerilogLoggerFactory(serilog, dispose: true);
    }

    private static string ResolveLogsDirectory()
    {
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "LogiPlusSwitcher");
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogiPlusSwitcher", "Logs");
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        return !string.IsNullOrEmpty(xdg)
            ? Path.Combine(xdg, "LogiPlusSwitcher")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "LogiPlusSwitcher");
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
