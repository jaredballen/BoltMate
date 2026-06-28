using System;
using System.Security.Cryptography;

namespace BoltMate.Core.Topology;

/// <summary>
/// Default <see cref="IPeerCryptoProvider"/> using AES-GCM with a
/// 96-bit random nonce and the 128-bit GCM tag. The key is resolved
/// on every call through a delegate so that signing in / out (which
/// flips the cached key) is observed without any explicit refresh
/// plumbing.
/// </summary>
/// <remarks>
/// AES-256 if the source returns 32 bytes; AES-128 if 16. Anything
/// else is treated as "no key" (= safer than guessing). 24-byte keys
/// would be AES-192 but we mint 256-bit on the server side so the
/// 192-bit branch is intentionally unreachable here.
/// </remarks>
public sealed class AesGcmPeerCryptoProvider : IPeerCryptoProvider
{
    public const int NonceSize = 12;
    public const int TagSize = 16;

    private readonly Func<byte[]?> _keyAccessor;

    public AesGcmPeerCryptoProvider(Func<byte[]?> keyAccessor)
    {
        ArgumentNullException.ThrowIfNull(keyAccessor);
        _keyAccessor = keyAccessor;
    }

    public bool HasKey
    {
        get
        {
            var k = _keyAccessor();
            return k is { Length: 32 or 16 };
        }
    }

    public bool TryEncrypt(ReadOnlySpan<byte> plaintext, out byte[] frame)
    {
        var key = _keyAccessor();
        if (key is not { Length: 32 or 16 })
        {
            frame = Array.Empty<byte>();
            return false;
        }

        frame = new byte[NonceSize + plaintext.Length + TagSize];
        var nonceSpan = frame.AsSpan(0, NonceSize);
        var cipherSpan = frame.AsSpan(NonceSize, plaintext.Length);
        var tagSpan = frame.AsSpan(NonceSize + plaintext.Length, TagSize);

        RandomNumberGenerator.Fill(nonceSpan);
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonceSpan, plaintext, cipherSpan, tagSpan);
        return true;
    }

    public bool TryDecrypt(ReadOnlySpan<byte> frame, out byte[] plaintext)
    {
        var key = _keyAccessor();
        if (key is not { Length: 32 or 16 } || frame.Length < NonceSize + TagSize)
        {
            plaintext = Array.Empty<byte>();
            return false;
        }

        var nonce = frame[..NonceSize];
        var tag = frame[^TagSize..];
        var cipher = frame[NonceSize..^TagSize];
        plaintext = new byte[cipher.Length];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, cipher, tag, plaintext);
            return true;
        }
        catch (CryptographicException)
        {
            // Tag mismatch — wrong key, tamper, or truncation. Drop.
            plaintext = Array.Empty<byte>();
            return false;
        }
    }
}
