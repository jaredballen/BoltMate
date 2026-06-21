using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BoltMate.Licensing.Tests;

internal sealed class TestKeys : IDisposable
{
    public RSA Rsa { get; } = RSA.Create(2048);

    public string PublicKeyPem => Rsa.ExportSubjectPublicKeyInfoPem();

    public string SignJwt(object header, object payload)
    {
        var headerB64 = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadB64 = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signing = Encoding.UTF8.GetBytes($"{headerB64}.{payloadB64}");
        var sig = Rsa.SignData(signing, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{headerB64}.{payloadB64}.{Base64Url(sig)}";
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => Rsa.Dispose();
}
