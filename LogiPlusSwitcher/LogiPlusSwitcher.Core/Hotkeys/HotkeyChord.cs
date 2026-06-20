namespace LogiPlusSwitcher.Core.Hotkeys;

/// <summary>
/// Platform-neutral global-hotkey description. Parsed from / written to a
/// human-readable string like <c>"Cmd+Ctrl+Shift+1"</c>. Platform-specific
/// services translate this into Carbon (macOS) or Win32 (Windows) keycodes.
/// </summary>
/// <param name="Modifiers">Modifier bitmask. <see cref="HotkeyModifiers.Command"/>
/// maps to <c>cmdKey</c> on macOS and <c>MOD_WIN</c> on Windows.</param>
/// <param name="Key">The non-modifier key. Limited to the keys we actually
/// support (number row 1..9, letter row, function keys F1..F12).</param>
public readonly record struct HotkeyChord(HotkeyModifiers Modifiers, HotkeyKey Key)
{
    /// <summary>Empty/invalid chord — useful as a default value.</summary>
    public static readonly HotkeyChord None = new(HotkeyModifiers.None, HotkeyKey.None);

    public bool IsValid => Key != HotkeyKey.None && Modifiers != HotkeyModifiers.None;

    /// <summary>Round-trippable string form, used for AppSettings persistence.</summary>
    public override string ToString()
    {
        if (!IsValid) return "";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Command)) parts.Add("Cmd");
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Option))  parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift))   parts.Add("Shift");
        parts.Add(KeyToString(Key));
        return string.Join("+", parts);
    }

    /// <summary>
    /// Parses <c>"Cmd+Ctrl+Shift+1"</c>-style strings. Token order doesn't
    /// matter; the last non-modifier token is the key. Returns <see cref="None"/>
    /// for unparseable input rather than throwing.
    /// </summary>
    public static HotkeyChord Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var mods = HotkeyModifiers.None;
        var key = HotkeyKey.None;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "cmd": case "command": case "win": case "super":
                    mods |= HotkeyModifiers.Command; break;
                case "ctrl": case "control":
                    mods |= HotkeyModifiers.Control; break;
                case "alt": case "option": case "opt":
                    mods |= HotkeyModifiers.Option; break;
                case "shift":
                    mods |= HotkeyModifiers.Shift; break;
                default:
                    if (TryParseKey(raw, out var k)) key = k;
                    break;
            }
        }
        return new HotkeyChord(mods, key);
    }

    private static bool TryParseKey(string token, out HotkeyKey key)
    {
        // 1..9 digit row
        if (token.Length == 1 && token[0] >= '1' && token[0] <= '9')
        {
            key = (HotkeyKey)((int)HotkeyKey.D1 + (token[0] - '1'));
            return true;
        }
        // 0 digit row
        if (token == "0") { key = HotkeyKey.D0; return true; }
        // Letters A..Z
        if (token.Length == 1 && char.IsLetter(token[0]))
        {
            key = (HotkeyKey)((int)HotkeyKey.A + (char.ToUpperInvariant(token[0]) - 'A'));
            return true;
        }
        // Function keys F1..F12
        if (token.Length >= 2 && (token[0] == 'F' || token[0] == 'f')
            && int.TryParse(token.AsSpan(1), out var n)
            && n is >= 1 and <= 12)
        {
            key = (HotkeyKey)((int)HotkeyKey.F1 + (n - 1));
            return true;
        }
        key = HotkeyKey.None;
        return false;
    }

    private static string KeyToString(HotkeyKey k) => k switch
    {
        >= HotkeyKey.D1 and <= HotkeyKey.D9 => ((int)k - (int)HotkeyKey.D1 + 1).ToString(),
        HotkeyKey.D0 => "0",
        >= HotkeyKey.A and <= HotkeyKey.Z => ((char)('A' + ((int)k - (int)HotkeyKey.A))).ToString(),
        >= HotkeyKey.F1 and <= HotkeyKey.F12 => "F" + ((int)k - (int)HotkeyKey.F1 + 1),
        _ => "?",
    };
}

[Flags]
public enum HotkeyModifiers
{
    None    = 0,
    Command = 1, // cmdKey on Mac, MOD_WIN on Win
    Control = 2,
    Option  = 4, // Alt on Win
    Shift   = 8,
}

/// <summary>
/// Limited keycode set. Keep this conservative — anything outside this enum
/// needs Carbon + Win32 keycode tables filled in on both platforms before it
/// can be a binding target.
/// </summary>
public enum HotkeyKey
{
    None = 0,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}
