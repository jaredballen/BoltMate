using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using LogiPlusSwitcher.Core.Hotkeys;

namespace LogiPlusSwitcher.App.Hotkeys;

/// <summary>
/// Linux placeholder. Real impls would split between X11 (XGrabKey) and
/// Wayland (no global hotkey API — needs compositor-specific protocol).
/// </summary>
public sealed class NoopGlobalHotkeyService : IGlobalHotkeyService
{
    private readonly Subject<int> _pressed = new();
    public bool IsSupported => false;
    public IObservable<int> Pressed => _pressed.AsObservable();
    public bool TryRegister(int id, HotkeyChord chord) => false;
    public void Unregister(int id) { }
    public void Dispose() => _pressed.Dispose();
}
