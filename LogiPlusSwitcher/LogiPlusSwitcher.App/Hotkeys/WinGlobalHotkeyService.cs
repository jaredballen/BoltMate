using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;
using LogiPlusSwitcher.Core.Hotkeys;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.App.Hotkeys;

/// <summary>
/// Win32 <c>RegisterHotKey</c>-based global hotkey listener. Lives on a
/// dedicated thread with a message-only window so we don't depend on
/// Avalonia's window lifecycle (tray apps may never realise a top-level HWND).
/// </summary>
public sealed class WinGlobalHotkeyService : IGlobalHotkeyService
{
    private readonly ILogger<WinGlobalHotkeyService> _logger;
    private readonly Subject<int> _pressed = new();
    private readonly Dictionary<int, HotkeyChord> _pending = new();
    private readonly object _gate = new();
    private readonly CompositeDisposable _disposables = new();
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _pumpThread;
    private IntPtr _hwnd;
    private uint _threadId;
    private bool _disposed;

    public bool IsSupported => true;
    public IObservable<int> Pressed => _pressed.AsObservable();

    public WinGlobalHotkeyService(ILogger<WinGlobalHotkeyService>? logger = null)
    {
        _logger = logger ?? NullLogger<WinGlobalHotkeyService>.Instance;
        _disposables.Add(_pressed);
    }

    /// <summary>Spawns the pump thread + message-only window. Idempotent.</summary>
    public void Start()
    {
        if (_pumpThread is not null || _disposed) return;
        _pumpThread = new Thread(PumpThread) { IsBackground = true, Name = "LogiPlusHotkeyPump" };
        _pumpThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
        // Drain any queued registrations.
        lock (_gate)
        {
            foreach (var kv in _pending.ToArray())
                TryRegisterCore(kv.Key, kv.Value);
            _pending.Clear();
        }
    }

    public bool TryRegister(int id, HotkeyChord chord)
    {
        if (_disposed) return false;
        if (!chord.IsValid) return false;
        Start();
        return TryRegisterCore(id, chord);
    }

    private bool TryRegisterCore(int id, HotkeyChord chord)
    {
        if (_hwnd == IntPtr.Zero)
        {
            // Pump thread not yet ready — queue.
            lock (_gate) _pending[id] = chord;
            return true;
        }

        var vk = TranslateKey(chord.Key);
        if (vk is null)
        {
            _logger.LogWarning("Hotkey {Id}: unsupported key {Key}", id, chord.Key);
            return false;
        }
        var mods = TranslateModifiers(chord.Modifiers) | MOD_NOREPEAT;

        // Marshal the RegisterHotKey call onto the pump thread so messages
        // arrive there. PostThreadMessage with WM_USER+1 (REGISTER) and pack
        // the args via a pinned struct... simpler: just call directly here;
        // RegisterHotKey targets the window by HWND, not the calling thread.
        var ok = RegisterHotKey(_hwnd, id, mods, vk.Value);
        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogWarning("RegisterHotKey id {Id} chord {Chord} failed: 0x{Err:X}", id, chord, err);
            return false;
        }
        _logger.LogInformation("Hotkey registered: id {Id} chord {Chord} (vk 0x{Vk:X2} mods 0x{Mods:X4})",
            id, chord, vk.Value, mods);
        return true;
    }

    public void Unregister(int id)
    {
        if (_disposed) return;
        if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, id);
        lock (_gate) _pending.Remove(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
        try { _pumpThread?.Join(TimeSpan.FromSeconds(1)); } catch { }
        _disposables.Dispose();
    }

    private void PumpThread()
    {
        _threadId = GetCurrentThreadId();

        // Create a message-only window so WM_HOTKEY has somewhere to go.
        _hwnd = CreateWindowEx(0, "STATIC", "LogiPlusSwitcher.Hotkeys",
            0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogError("CreateWindowEx for hotkey pump failed: 0x{Err:X}", err);
            _ready.Set();
            return;
        }

        _ready.Set();

        // Standard Win32 message loop.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == WM_HOTKEY)
            {
                var id = msg.WParam.ToInt32();
                _logger.LogDebug("Win32 hotkey fired: id {Id}", id);
                try { _pressed.OnNext(id); } catch { }
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private static uint? TranslateKey(HotkeyKey k) => k switch
    {
        HotkeyKey.D0 => 0x30, HotkeyKey.D1 => 0x31, HotkeyKey.D2 => 0x32,
        HotkeyKey.D3 => 0x33, HotkeyKey.D4 => 0x34, HotkeyKey.D5 => 0x35,
        HotkeyKey.D6 => 0x36, HotkeyKey.D7 => 0x37, HotkeyKey.D8 => 0x38,
        HotkeyKey.D9 => 0x39,
        >= HotkeyKey.A and <= HotkeyKey.Z => (uint)(0x41 + ((int)k - (int)HotkeyKey.A)),
        >= HotkeyKey.F1 and <= HotkeyKey.F12 => (uint)(0x70 + ((int)k - (int)HotkeyKey.F1)),
        _ => null,
    };

    private static uint TranslateModifiers(HotkeyModifiers m)
    {
        uint result = 0;
        if (m.HasFlag(HotkeyModifiers.Command)) result |= MOD_WIN;
        if (m.HasFlag(HotkeyModifiers.Control)) result |= MOD_CONTROL;
        if (m.HasFlag(HotkeyModifiers.Option))  result |= MOD_ALT;
        if (m.HasFlag(HotkeyModifiers.Shift))   result |= MOD_SHIFT;
        return result;
    }

    // --- P/Invoke ------------------------------------------------------

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
