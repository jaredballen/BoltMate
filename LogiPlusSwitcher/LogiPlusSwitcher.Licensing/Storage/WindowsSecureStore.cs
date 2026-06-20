using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LogiPlusSwitcher.Licensing.Storage;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSecureStore : ISecureStore
{
    private readonly string _root;

    public WindowsSecureStore(string serviceName)
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            serviceName,
            "secrets");
        Directory.CreateDirectory(_root);
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
            return Task.FromResult<string?>(null);

        var encrypted = File.ReadAllBytes(path);
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(key), encrypted);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        var safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_root, $"{safe}.bin");
    }
}
