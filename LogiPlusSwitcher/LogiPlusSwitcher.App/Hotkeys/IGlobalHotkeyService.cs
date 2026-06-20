using System;
using LogiPlusSwitcher.Core.Hotkeys;

namespace LogiPlusSwitcher.App.Hotkeys;

/// <summary>
/// Platform-agnostic global hotkey registration. Implementations:
/// macOS — Carbon <c>RegisterEventHotKey</c>;
/// Windows — Win32 <c>RegisterHotKey</c> on a message-only window;
/// Linux — currently noop (X11 / Wayland support TBD).
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    /// <summary>True if this platform actually supports global hotkeys (false on Linux noop).</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Registers a chord. The <paramref name="id"/> is opaque — it is the
    /// integer surfaced in <see cref="Pressed"/> when the chord fires.
    /// Returns false if registration failed (chord conflict on Win, accessibility
    /// permission missing on Mac, etc.).
    /// </summary>
    bool TryRegister(int id, HotkeyChord chord);

    /// <summary>Unregisters a previously-registered chord. Idempotent.</summary>
    void Unregister(int id);

    /// <summary>Hot stream of hotkey IDs as chords fire. Suppresses key repeat where possible.</summary>
    IObservable<int> Pressed { get; }
}
