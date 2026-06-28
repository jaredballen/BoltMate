using System;
using BoltMate.Core.Topology;
using Xunit;

namespace BoltMate.Tests;

public sealed class AesGcmPeerCryptoProviderTests
{
    private static byte[] Key32() => Convert.FromBase64String("3qgZqg+nL7C8r9NQO0xJ8t6h0a2J0qQF1bD5L9wQYwI=");

    [Fact]
    public void Round_trips_plaintext_when_key_present()
    {
        var key = Key32();
        var crypto = new AesGcmPeerCryptoProvider(() => key);
        var plaintext = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        Assert.True(crypto.TryEncrypt(plaintext, out var frame));
        Assert.Equal(plaintext.Length + AesGcmPeerCryptoProvider.NonceSize + AesGcmPeerCryptoProvider.TagSize, frame.Length);

        Assert.True(crypto.TryDecrypt(frame, out var roundTripped));
        Assert.Equal(plaintext, roundTripped);
    }

    [Fact]
    public void Different_nonces_per_call()
    {
        var key = Key32();
        var crypto = new AesGcmPeerCryptoProvider(() => key);
        var plaintext = new byte[] { 9, 9, 9 };

        crypto.TryEncrypt(plaintext, out var a);
        crypto.TryEncrypt(plaintext, out var b);

        // First 12 bytes are the nonce; same plaintext + same key MUST
        // never produce the same nonce (catastrophic for AES-GCM).
        Assert.NotEqual(a.AsSpan(0, 12).ToArray(), b.AsSpan(0, 12).ToArray());
    }

    [Fact]
    public void No_key_returns_false_on_both_directions()
    {
        var crypto = new AesGcmPeerCryptoProvider(() => null);

        Assert.False(crypto.HasKey);
        Assert.False(crypto.TryEncrypt(new byte[] { 1 }, out var enc));
        Assert.Empty(enc);
        Assert.False(crypto.TryDecrypt(new byte[64], out var dec));
        Assert.Empty(dec);
    }

    [Fact]
    public void Wrong_key_fails_decrypt()
    {
        var keyA = Key32();
        var keyB = new byte[32];
        Buffer.BlockCopy(keyA, 0, keyB, 0, 32);
        keyB[0] ^= 0xff;

        var encryptor = new AesGcmPeerCryptoProvider(() => keyA);
        encryptor.TryEncrypt(new byte[] { 42 }, out var frame);

        var decryptor = new AesGcmPeerCryptoProvider(() => keyB);
        Assert.False(decryptor.TryDecrypt(frame, out var pt));
        Assert.Empty(pt);
    }

    [Fact]
    public void Tampered_tag_fails_decrypt()
    {
        var key = Key32();
        var crypto = new AesGcmPeerCryptoProvider(() => key);
        crypto.TryEncrypt(new byte[] { 100, 200 }, out var frame);

        // Flip a bit in the trailing tag.
        frame[^1] ^= 0xff;

        Assert.False(crypto.TryDecrypt(frame, out var pt));
        Assert.Empty(pt);
    }

    [Fact]
    public void Truncated_frame_fails_decrypt()
    {
        var key = Key32();
        var crypto = new AesGcmPeerCryptoProvider(() => key);

        var tooShort = new byte[AesGcmPeerCryptoProvider.NonceSize + AesGcmPeerCryptoProvider.TagSize - 1];
        Assert.False(crypto.TryDecrypt(tooShort, out var pt));
        Assert.Empty(pt);
    }

    [Fact]
    public void Key_rotation_observed_per_call()
    {
        byte[]? key = null;
        var crypto = new AesGcmPeerCryptoProvider(() => key);

        Assert.False(crypto.TryEncrypt(new byte[] { 1 }, out _));

        key = Key32();
        Assert.True(crypto.TryEncrypt(new byte[] { 1 }, out var frame));
        Assert.True(crypto.TryDecrypt(frame, out _));

        key = null;
        Assert.False(crypto.TryDecrypt(frame, out _));
    }
}
