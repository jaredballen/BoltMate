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
/// Maps each <see cref="HotkeyBinding"/> in settings to a global chord
/// registration. On press, calls <see cref="SwitcherService.RequestTopologyFanOut"/>
/// with the bound target BLE — same topology lookup as the Easy-Switch / Flow /
/// UDP correlator paths, so every device routes to the slot that points at the
/// chosen target (slot index may differ across devices).
/// </summary>
/// <remarks>
/// Each binding's index in <see cref="HotkeySettings.Bindings"/> is its
/// registration id (offset by <see cref="HotkeyIdBase"/>). Unbound bindings
/// (TargetBleHex == null) are skipped — the chord is still pre-defined for
/// the UI but no hotkey is registered with the OS until the user picks a target.
/// </remarks>
public sealed class HotkeyOrchestrator : IDisposable
{
    private const int HotkeyIdBase = 1000;

    private readonly IGlobalHotkeyService _hotkeys;
    private readonly SwitcherService _switcher;
    private readonly HotkeySettings _settings;
    private readonly ILogger<HotkeyOrchestrator> _logger;
    private readonly CompositeDisposable _disposables = new();
    private readonly Dictionary<int, HotkeyBinding> _registeredById = new();

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

    /// <summary>Registers every (bound) chord in the current settings. Safe to call repeatedly.</summary>
    public void Apply()
    {
        ClearAll();

        if (!_settings.Enabled)
        {
            _logger.LogInformation("Hotkeys disabled in settings");
            return;
        }
        if (!_hotkeys.IsSupported)
        {
            _logger.LogInformation("Hotkeys not supported on this platform");
            return;
        }

        for (var i = 0; i < _settings.Bindings.Count; i++)
        {
            var binding = _settings.Bindings[i];
            if (string.IsNullOrWhiteSpace(binding.TargetBleHex))
            {
                _logger.LogInformation("Chord {Chord} is unbound — skipping registration", binding.Chord);
                continue;
            }
            var chord = HotkeyChord.Parse(binding.Chord);
            if (!chord.IsValid)
            {
                _logger.LogWarning("Skipping invalid chord '{Raw}' (target {Ble})", binding.Chord, binding.TargetBleHex);
                continue;
            }

            var id = HotkeyIdBase + i;
            if (_hotkeys.TryRegister(id, chord))
            {
                _registeredById[id] = binding;
                _logger.LogInformation("Bound {Chord} -> target BLE {Ble} ({Label})",
                    binding.Chord, binding.TargetBleHex, binding.TargetLabel ?? "(no label)");
            }
        }
    }

    public void Dispose()
    {
        ClearAll();
        _disposables.Dispose();
    }

    private void ClearAll()
    {
        foreach (var id in _registeredById.Keys.ToArray())
            _hotkeys.Unregister(id);
        _registeredById.Clear();
    }

    private void OnPressed(int id)
    {
        if (!_registeredById.TryGetValue(id, out var binding))
        {
            _logger.LogWarning("Hotkey {Id} fired but no binding found", id);
            return;
        }
        if (string.IsNullOrWhiteSpace(binding.TargetBleHex))
        {
            _logger.LogWarning("Hotkey {Id} fired but target BLE is unbound", id);
            return;
        }

        _logger.LogInformation("Hotkey {Chord} -> target BLE {Ble} ({Label})",
            binding.Chord, binding.TargetBleHex, binding.TargetLabel ?? "(no label)");
        try
        {
            _switcher.RequestTopologyFanOut(
                binding.TargetBleHex.ToLowerInvariant(),
                originatingDeviceWpid: null,
                source: FanOutSource.UserHotkey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hotkey fan-out for chord {Chord} threw", binding.Chord);
        }
    }
}
