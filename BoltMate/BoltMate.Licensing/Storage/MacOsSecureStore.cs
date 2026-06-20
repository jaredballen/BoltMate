using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BoltMate.Licensing.Storage;

[SupportedOSPlatform("macos")]
internal sealed class MacOsSecureStore : ISecureStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;

    private readonly string _service;

    public MacOsSecureStore(string serviceName) => _service = serviceName;

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var query = BuildQuery(key, returnData: true);
        try
        {
            var status = SecItemCopyMatching(query, out var resultRef);
            if (status == ErrSecItemNotFound) return Task.FromResult<string?>(null);
            if (status != ErrSecSuccess) throw new InvalidOperationException($"SecItemCopyMatching failed: {status}");

            try
            {
                var length = (int)CFDataGetLength(resultRef);
                var ptr = CFDataGetBytePtr(resultRef);
                var bytes = new byte[length];
                Marshal.Copy(ptr, bytes, 0, length);
                return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
            }
            finally
            {
                CFRelease(resultRef);
            }
        }
        finally
        {
            CFRelease(query);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var dataRef = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
        var add = BuildQuery(key, returnData: false, dataToStore: dataRef);
        try
        {
            var status = SecItemAdd(add, IntPtr.Zero);
            if (status == ErrSecDuplicateItem)
            {
                var search = BuildQuery(key, returnData: false);
                var update = CFDictionaryWithData(dataRef);
                try
                {
                    status = SecItemUpdate(search, update);
                }
                finally
                {
                    CFRelease(search);
                    CFRelease(update);
                }
            }
            if (status != ErrSecSuccess)
                throw new InvalidOperationException($"SecItemAdd/Update failed: {status}");
        }
        finally
        {
            CFRelease(add);
            CFRelease(dataRef);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var query = BuildQuery(key, returnData: false);
        try
        {
            var status = SecItemDelete(query);
            if (status != ErrSecSuccess && status != ErrSecItemNotFound)
                throw new InvalidOperationException($"SecItemDelete failed: {status}");
        }
        finally
        {
            CFRelease(query);
        }
        return Task.CompletedTask;
    }

    private IntPtr BuildQuery(string account, bool returnData, IntPtr dataToStore = default)
    {
        var dict = CFDictionaryCreateMutable(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);

        var classKey = LoadString("kSecClass");
        var classGeneric = LoadString("kSecClassGenericPassword");
        CFDictionarySetValue(dict, classKey, classGeneric);

        var serviceKey = LoadString("kSecAttrService");
        var serviceVal = CFStringCreate(_service);
        CFDictionarySetValue(dict, serviceKey, serviceVal);
        CFRelease(serviceVal);

        var accountKey = LoadString("kSecAttrAccount");
        var accountVal = CFStringCreate(account);
        CFDictionarySetValue(dict, accountKey, accountVal);
        CFRelease(accountVal);

        if (returnData)
        {
            var returnDataKey = LoadString("kSecReturnData");
            var trueRef = LoadConstant("kCFBooleanTrue");
            CFDictionarySetValue(dict, returnDataKey, trueRef);
        }

        if (dataToStore != IntPtr.Zero)
        {
            var valueDataKey = LoadString("kSecValueData");
            CFDictionarySetValue(dict, valueDataKey, dataToStore);
        }

        return dict;
    }

    private static IntPtr CFDictionaryWithData(IntPtr data)
    {
        var dict = CFDictionaryCreateMutable(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
        var valueDataKey = LoadString("kSecValueData");
        CFDictionarySetValue(dict, valueDataKey, data);
        return dict;
    }

    private static IntPtr CFStringCreate(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, 0x08000100u, false);
    }

    private static IntPtr LoadString(string name)
    {
        var handle = NativeLibrary.Load(Security);
        var symbol = NativeLibrary.GetExport(handle, name);
        return Marshal.ReadIntPtr(symbol);
    }

    private static IntPtr LoadConstant(string name)
    {
        var handle = NativeLibrary.Load(CoreFoundation);
        var symbol = NativeLibrary.GetExport(handle, name);
        return Marshal.ReadIntPtr(symbol);
    }

    [DllImport(Security)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Security)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);
    [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);
    [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr handle);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, IntPtr capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CoreFoundation)] private static extern void CFDictionarySetValue(IntPtr dict, IntPtr key, IntPtr value);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithBytes(IntPtr alloc, byte[] bytes, long numBytes, uint encoding, [MarshalAs(UnmanagedType.I1)] bool isExternal);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, long length);
    [DllImport(CoreFoundation)] private static extern long CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
}
