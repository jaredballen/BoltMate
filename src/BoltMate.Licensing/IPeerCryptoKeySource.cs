using System;

namespace BoltMate.Licensing;

/// <summary>
/// Read-side view of the AES-GCM SyncKey cached by <see cref="LicenseGate"/>.
/// Exposed as its own narrow interface so the BoltMate.Core crypto layer can
/// resolve a key source without having to take a dependency on the entire
/// licensing stack.
/// </summary>
public interface IPeerCryptoKeySource
{
    /// <summary>
    /// Returns the 32-byte AES-256 SyncKey for the current account, or
    /// null when no license is loaded / the user is signed out. Callers
    /// MUST treat null as "no peer crypto available" — typically by
    /// short-circuiting all encrypt + decrypt operations to pass-through
    /// or drop semantics depending on the consumer.
    /// </summary>
    byte[]? GetCurrentKey();
}
