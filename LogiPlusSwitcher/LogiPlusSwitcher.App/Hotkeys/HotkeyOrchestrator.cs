using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using LogiPlusSwitcher.Core;
using LogiPlusSwitcher.Core.Hotkeys;
using LogiPlusSwitcher.Core.Switcher;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.App.Hotkeys;

/// <summary>
/// Reads <see cref="HotkeySettings"/>, registers each chord with the
/// platform <see cref="IGlobalHotkeyService"/>, and routes presses to
/// <see cref="SwitcherService.RequestUserFanOut"/>.
/// </summary>
/// <remarks>
/// Each hotkey gets a numeric id derived from the target host slot + 1000
/// (so id 1000 = host 0, 1001 = host 1, …). The +1000 offset just gives
/// us room to add other hotkey categories later without colliding.
/// </remarks>
public sealed class HotkeyOrchestrator : IDisposable
{
    private const int HotkeyIdBase = 1000;

    private readonly IGlobalHotkeyService _hotkeys;
    private readonly SwitcherService _switcher;
    private readonly HotkeySettings _settings;
    private readonly ILogger<HotkeyOrchestrator> _logger;
    private readonly CompositeDisposable _disposables = new();
    private readonly HashSet<int> _registeredIds = new();

    public HotkeyOrchestrator(
        IGlobalHotkeyService hotkeys,
        SwitcherService switcher,
        HotkeySettings settings,
        ILogger<HotkeyOrchestrator>? logger = null)
    {
        _hotkeys = hotkeys;
        _switcher = switcher;
        _settings = settings;
        _logger = logger ?? NullLogger<HotkeyOrchestrator>.Instance;

        _disposables.Add(_hotkeys.Pressed.Subscribe(OnPressed));
    }

    /// <summary>Registers every chord in the current settings. Safe to call repeatedly.</summary>
    public void Apply()
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Hotkeys disabled in settings; clearing any prior registrations");
            ClearAll();
            return;
        }
        if (!_hotkeys.IsSupported)
        {
            _logger.LogInformation("Hotkeys not supported on this platform");
            return;
        }

        ClearAll();

        foreach (var kv in _settings.HostBindings)
        {
            var slot = kv.Key;
            var chord = HotkeyChord.Parse(kv.Value);
            if (!chord.IsValid)
            {
                _logger.LogWarning("Skipping invalid chord '{Raw}' for slot {Slot}", kv.Value, slot);
                continue;
            }
            var id = HotkeyIdBase + slot;
            if (_hotkeys.TryRegister(id, chord))
                _registeredIds.Add(id);
        }
    }

    public void Dispose()
    {
        ClearAll();
        _disposables.Dispose();
    }

    private void ClearAll()
    {
        foreach (var id in _registeredIds.ToArray())
            _hotkeys.Unregister(id);
        _registeredIds.Clear();
    }

    private void OnPressed(int id)
    {
        if (id < HotkeyIdBase) return;
        var slot = (byte)(id - HotkeyIdBase);
        _logger.LogInformation("Hotkey -> host slot {Slot}", slot);
        try
        {
            _switcher.RequestUserFanOut(slot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "User fan-out for slot {Slot} threw", slot);
        }
    }
}
