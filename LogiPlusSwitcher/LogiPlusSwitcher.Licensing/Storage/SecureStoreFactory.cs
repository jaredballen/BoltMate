using System;
using System.Runtime.InteropServices;

namespace LogiPlusSwitcher.Licensing.Storage;

public static class SecureStoreFactory
{
    public static ISecureStore CreateForCurrentPlatform(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (OperatingSystem.IsWindows())
            return new WindowsSecureStore(serviceName);

        if (OperatingSystem.IsMacOS())
            return new MacOsSecureStore(serviceName);

        throw new PlatformNotSupportedException(
            $"Secure store not implemented for {RuntimeInformation.OSDescription}. " +
            "Windows and macOS are the supported platforms.");
    }
}
