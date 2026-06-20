namespace BoltMate.Core;

/// <summary>
/// Per-platform on-disk paths used by the app. Centralised so the CLI, the
/// tray app, and the future installer all agree on where settings, logs,
/// and backup files live.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "BoltMate";

    /// <summary>User-readable settings (JSON config). macOS: <c>~/Library/Application Support/BoltMate</c>.</summary>
    public static string SettingsDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", AppFolderName)
        : OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", AppFolderName);

    /// <summary>Logs directory. macOS: <c>~/Library/Logs/BoltMate</c>.</summary>
    public static string LogsDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", AppFolderName)
        : OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName, "Logs")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", AppFolderName);

    /// <summary>Default backup directory.</summary>
    public static string BackupsDirectory => Path.Combine(SettingsDirectory, "Backups");

    /// <summary>Path to the user's settings JSON file.</summary>
    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Ensures the settings + backups directories exist. Idempotent.</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
