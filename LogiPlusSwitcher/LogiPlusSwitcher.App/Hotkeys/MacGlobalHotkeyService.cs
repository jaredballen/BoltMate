using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using LogiPlusSwitcher.Core.Hotkeys;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.App.Hotkeys;

/// <summary>
/// Carbon <c>RegisterEventHotKey</c>-based global hotkey listener. Carbon's
/// HIToolbox is technically deprecated but still ships on Apple Silicon and
/// remains the standard path used by Alfred, Spotlight clones, and most
/// macOS hotkey libraries. CGEventTap is the alternative but requires
/// Accessibility permission and is more invasive.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="Start"/> installs a single Carbon event handler on
/// the application event target (Avalonia's main NSApp event loop pumps
/// Carbon events automatically). Each <see cref="TryRegister"/> call wraps
/// <c>RegisterEventHotKey</c> and stores the returned <c>EventHotKeyRef</c>
/// for later unregistration.
/// </remarks>
public sealed class MacGlobalHotkeyService : IGlobalHotkeyService
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    private readonly ILogger<MacGlobalHotkeyService> _logger;
    private readonly Subject<int> _pressed = new();
    private readonly Dictionary<int, IntPtr> _registered = new();
    private readonly object _gate = new();
    private readonly CompositeDisposable _disposables = new();
    private EventHandlerUPP? _handler;
    private IntPtr _handlerRef;
    private GCHandle _handlerGcHandle;
    private bool _started;
    private bool _disposed;

    public bool IsSupported => true;
    public IObservable<int> Pressed => _pressed.AsObservable();

    public MacGlobalHotkeyService(ILogger<MacGlobalHotkeyService>? logger = null)
    {
        _logger = logger ?? NullLogger<MacGlobalHotkeyService>.Instance;
        _disposables.Add(_pressed);
    }

    /// <summary>Installs the shared Carbon event handler. Idempotent.</summary>
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;

        _handler = OnHotkeyEvent;
        _handlerGcHandle = GCHandle.Alloc(_handler);

        var spec = new EventTypeSpec { EventClass = kEventClassKeyboard, EventKind = kEventHotKeyPressed };
        var target = GetApplicationEventTarget();
        var status = InstallEventHandler(target, _handler, 1, ref spec, IntPtr.Zero, out _handlerRef);
        if (status != 0)
        {
            _logger.LogWarning("InstallEventHandler returned {Status}; hotkeys will not fire", status);
        }
        else
        {
            _logger.LogInformation("Carbon hotkey handler installed");
        }
    }

    public bool TryRegister(int id, HotkeyChord chord)
    {
        if (_disposed) return false;
        if (!chord.IsValid) return false;
        Start();

        var keyCode = TranslateKey(chord.Key);
        if (keyCode is null)
        {
            _logger.LogWarning("Hotkey {Id}: unsupported key {Key}", id, chord.Key);
            return false;
        }

        var modifiers = TranslateModifiers(chord.Modifiers);

        lock (_gate)
        {
            Unregister_NoLock(id);

            var hotKeyId = new EventHotKeyID
            {
                Signature = HotkeySignature,
                Id = (uint)id,
            };
            var status = RegisterEventHotKey(keyCode.Value, modifiers, hotKeyId,
                GetApplicationEventTarget(), 0, out var hotKeyRef);
            if (status != 0)
            {
                _logger.LogWarning("RegisterEventHotKey for {Chord} failed: status {Status}", chord, status);
                return false;
            }
            _registered[id] = hotKeyRef;
            _logger.LogInformation("Hotkey registered: id {Id} chord {Chord} (keyCode 0x{Code:X2} mods 0x{Mods:X4})",
                id, chord, keyCode.Value, modifiers);
            return true;
        }
    }

    public void Unregister(int id)
    {
        if (_disposed) return;
        lock (_gate)
        {
            Unregister_NoLock(id);
        }
    }

    private void Unregister_NoLock(int id)
    {
        if (!_registered.Remove(id, out var hotKeyRef)) return;
        var status = UnregisterEventHotKey(hotKeyRef);
        if (status != 0)
            _logger.LogDebug("UnregisterEventHotKey {Id} status {Status}", id, status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var hotKeyRef in _registered.Values)
            {
                try { UnregisterEventHotKey(hotKeyRef); } catch { }
            }
            _registered.Clear();
        }
        if (_handlerRef != IntPtr.Zero)
        {
            try { RemoveEventHandler(_handlerRef); } catch { }
            _handlerRef = IntPtr.Zero;
        }
        if (_handlerGcHandle.IsAllocated) _handlerGcHandle.Free();
        _disposables.Dispose();
    }

    private int OnHotkeyEvent(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
    {
        try
        {
            var size = (uint)Marshal.SizeOf<EventHotKeyID>();
            var bufferSize = IntPtr.Zero;
            var buffer = new EventHotKeyID();
            var status = GetEventParameter(theEvent,
                kEventParamDirectObject,
                typeEventHotKeyID,
                IntPtr.Zero,
                size,
                ref bufferSize,
                ref buffer);
            if (status == 0)
            {
                var id = (int)buffer.Id;
                _logger.LogDebug("Carbon hotkey fired: id {Id}", id);
                _pressed.OnNext(id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OnHotkeyEvent dispatch failed");
        }
        return 0; // noErr; let other handlers see it too
    }

    /// <summary>
    /// Translates our <see cref="HotkeyKey"/> to the Carbon virtual keycode.
    /// Values from Inside Macintosh / IOKit Events.h
    /// (kVK_ANSI_*). ARM macOS uses identical codes.
    /// </summary>
    private static uint? TranslateKey(HotkeyKey k) => k switch
    {
        HotkeyKey.D1 => 0x12, HotkeyKey.D2 => 0x13, HotkeyKey.D3 => 0x14,
        HotkeyKey.D4 => 0x15, HotkeyKey.D5 => 0x17, HotkeyKey.D6 => 0x16,
        HotkeyKey.D7 => 0x1A, HotkeyKey.D8 => 0x1C, HotkeyKey.D9 => 0x19,
        HotkeyKey.D0 => 0x1D,
        HotkeyKey.A => 0x00, HotkeyKey.B => 0x0B, HotkeyKey.C => 0x08,
        HotkeyKey.D => 0x02, HotkeyKey.E => 0x0E, HotkeyKey.F => 0x03,
        HotkeyKey.G => 0x05, HotkeyKey.H => 0x04, HotkeyKey.I => 0x22,
        HotkeyKey.J => 0x26, HotkeyKey.K => 0x28, HotkeyKey.L => 0x25,
        HotkeyKey.M => 0x2E, HotkeyKey.N => 0x2D, HotkeyKey.O => 0x1F,
        HotkeyKey.P => 0x23, HotkeyKey.Q => 0x0C, HotkeyKey.R => 0x0F,
        HotkeyKey.S => 0x01, HotkeyKey.T => 0x11, HotkeyKey.U => 0x20,
        HotkeyKey.V => 0x09, HotkeyKey.W => 0x0D, HotkeyKey.X => 0x07,
        HotkeyKey.Y => 0x10, HotkeyKey.Z => 0x06,
        HotkeyKey.F1 => 0x7A, HotkeyKey.F2 => 0x78, HotkeyKey.F3 => 0x63,
        HotkeyKey.F4 => 0x76, HotkeyKey.F5 => 0x60, HotkeyKey.F6 => 0x61,
        HotkeyKey.F7 => 0x62, HotkeyKey.F8 => 0x64, HotkeyKey.F9 => 0x65,
        HotkeyKey.F10 => 0x6D, HotkeyKey.F11 => 0x67, HotkeyKey.F12 => 0x6F,
        _ => null,
    };

    /// <summary>Carbon modifier mask. Values from Carbon Events.h.</summary>
    private static uint TranslateModifiers(HotkeyModifiers m)
    {
        uint result = 0;
        if (m.HasFlag(HotkeyModifiers.Command)) result |= 0x100; // cmdKey
        if (m.HasFlag(HotkeyModifiers.Shift))   result |= 0x200; // shiftKey
        if (m.HasFlag(HotkeyModifiers.Option))  result |= 0x800; // optionKey
        if (m.HasFlag(HotkeyModifiers.Control)) result |= 0x1000; // controlKey
        return result;
    }

    // --- P/Invoke ------------------------------------------------------

    private const uint kEventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint kEventHotKeyPressed = 5;
    private const uint typeEventHotKeyID = 0x686B6964; // 'hkid'
    private const uint kEventParamDirectObject = 0x2D2D2D2D; // '----'
    private const uint HotkeySignature = 0x4C504853; // 'LPHS' (LogiPlusSwitcher)

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    private delegate int EventHandlerUPP(IntPtr nextHandler, IntPtr theEvent, IntPtr userData);

    [DllImport(Carbon, EntryPoint = "GetApplicationEventTarget")]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport(Carbon, EntryPoint = "InstallEventHandler")]
    private static extern int InstallEventHandler(
        IntPtr target,
        EventHandlerUPP handler,
        uint numTypes,
        ref EventTypeSpec spec,
        IntPtr userData,
        out IntPtr handlerRef);

    [DllImport(Carbon, EntryPoint = "RemoveEventHandler")]
    private static extern int RemoveEventHandler(IntPtr handlerRef);

    [DllImport(Carbon, EntryPoint = "RegisterEventHotKey")]
    private static extern int RegisterEventHotKey(
        uint keyCode,
        uint modifiers,
        EventHotKeyID id,
        IntPtr target,
        uint options,
        out IntPtr hotKeyRef);

    [DllImport(Carbon, EntryPoint = "UnregisterEventHotKey")]
    private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Carbon, EntryPoint = "GetEventParameter")]
    private static extern int GetEventParameter(
        IntPtr theEvent,
        uint name,
        uint desiredType,
        IntPtr actualType,
        uint bufferSize,
        ref IntPtr actualSize,
        ref EventHotKeyID outData);
}
