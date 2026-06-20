using System;
using Microsoft.Extensions.Logging;

namespace LogiPlusSwitcher.App.Hotkeys;

public static class GlobalHotkeyServiceFactory
{
    public static IGlobalHotkeyService Create(ILoggerFactory loggerFactory)
    {
        if (OperatingSystem.IsMacOS())
            return new MacGlobalHotkeyService(loggerFactory.CreateLogger<MacGlobalHotkeyService>());
        if (OperatingSystem.IsWindows())
            return new WinGlobalHotkeyService(loggerFactory.CreateLogger<WinGlobalHotkeyService>());
        return new NoopGlobalHotkeyService();
    }
}
