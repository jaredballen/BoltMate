using Avalonia;
using System;
using System.IO;
using BoltMate.Core;

namespace BoltMate.App;

class Program
{
    // Held for process lifetime so the OS keeps the exclusive lock. Released
    // automatically when the process dies — handles crashes too.
    private static FileStream? _instanceLock;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Single-instance guard. Without this, an in-flight welcome wizard
        // collides with a second copy spawned by the freshly-loaded
        // LaunchAgent right after "Get started" installs autostart — two
        // welcome windows appear.
        if (!TryAcquireInstanceLock())
            return;

        // Last-resort unhandled exception sink so we always get a written
        // record of what threw — without these hooks, Avalonia / .NET
        // route fatal exceptions to the OS crash reporter only.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            TryWriteCrashRecord("UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            TryWriteCrashRecord("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // macOS: set NSProcessInfo.processName BEFORE Avalonia bootstraps so
        // its native menu builder picks up the right title. Without this the
        // app menu reads "Avalonia Application".
        MacActivationPolicy.SetProcessName("BoltMate");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void TryWriteCrashRecord(string kind, Exception? ex)
    {
        try
        {
            var dir = AppPaths.LogsDirectory;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"boltmate-crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(path,
                $"BoltMate.App {kind} @ {DateTime.UtcNow:O}\n\n{ex}\n");
        }
        catch { /* best-effort */ }
    }

    private static bool TryAcquireInstanceLock()
    {
        try
        {
            AppPaths.EnsureDirectories();
            var lockPath = Path.Combine(AppPaths.SettingsDirectory, "boltmate-app.lock");
            _instanceLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
