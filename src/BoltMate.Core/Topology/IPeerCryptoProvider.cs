namespace BoltMate.Core.Topology;

/// <summary>
/// Wraps + unwraps the cross-machine UDP / TCP wire payloads with the
/// per-account AES-GCM <c>SyncKey</c>.
/// </summary>
/// <remarks>
/// <para>Wire format produced by <see cref="TryEncrypt"/>:</para>
/// <code>
/// nonce(12 bytes) || ciphertext(len bytes) || tag(16 bytes)
/// </code>
/// <para>"No key" (user not signed in, or licensing layer not registered)
/// is the unambiguous signal that peer comms must NOT flow. Both
/// methods return <c>false</c> in that state — callers gate
/// outbound on <see cref="TryEncrypt"/> success and silently drop any
/// inbound that fails <see cref="TryDecrypt"/>.</para>
/// </remarks>
public interface IPeerCryptoProvider
{
    /// <summary>
    /// True if a key is currently loaded. Cheap probe for callers that
    /// want to decide whether to start the network stack at all.
    /// </summary>
    bool HasKey { get; }

    /// <summary>
    /// Wraps <paramref name="plaintext"/> into the envelope. Returns
    /// false (with <paramref name="frame"/> = empty) when no key is
    /// loaded — caller should not transmit.
    /// </summary>
    bool TryEncrypt(System.ReadOnlySpan<byte> plaintext, out byte[] frame);

    /// <summary>
    /// Unwraps <paramref name="frame"/> back to plaintext. Returns
    /// false on any failure — wrong key, truncated frame, tag
    /// mismatch (= tamper or wrong account). Caller should drop the
    /// inbound message silently.
    /// </summary>
    bool TryDecrypt(System.ReadOnlySpan<byte> frame, out byte[] plaintext);
}
