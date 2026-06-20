using LogiPlusSwitcher.Core.Hotkeys;
using Xunit;

namespace LogiPlusSwitcher.Tests;

public class HotkeyChordTests
{
    [Theory]
    [InlineData("Cmd+Ctrl+Shift+1", HotkeyKey.D1)]
    [InlineData("cmd+ctrl+shift+2", HotkeyKey.D2)]
    [InlineData("Shift+Cmd+Ctrl+3", HotkeyKey.D3)]
    [InlineData("Cmd+A",            HotkeyKey.A)]
    [InlineData("Ctrl+Alt+F5",      HotkeyKey.F5)]
    [InlineData("Win+Shift+0",      HotkeyKey.D0)]
    public void Parse_round_trips_common_chords(string text, HotkeyKey expectedKey)
    {
        var chord = HotkeyChord.Parse(text);
        Assert.True(chord.IsValid);
        Assert.Equal(expectedKey, chord.Key);
    }

    [Fact]
    public void Parse_uppercase_lowercase_modifier_aliases()
    {
        var c = HotkeyChord.Parse("Command+Option+Control+Shift+Q");
        Assert.Equal(HotkeyKey.Q, c.Key);
        Assert.True(c.Modifiers.HasFlag(HotkeyModifiers.Command));
        Assert.True(c.Modifiers.HasFlag(HotkeyModifiers.Option));
        Assert.True(c.Modifiers.HasFlag(HotkeyModifiers.Control));
        Assert.True(c.Modifiers.HasFlag(HotkeyModifiers.Shift));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("Ctrl+")]
    [InlineData("Shift")]
    public void Parse_returns_invalid_for_bad_input(string? text)
    {
        var c = HotkeyChord.Parse(text);
        Assert.False(c.IsValid);
    }

    [Fact]
    public void ToString_round_trips()
    {
        var c1 = HotkeyChord.Parse("Cmd+Ctrl+Shift+1");
        var c2 = HotkeyChord.Parse(c1.ToString());
        Assert.Equal(c1, c2);
    }

    [Fact]
    public void Win_alias_maps_to_Command()
    {
        var c = HotkeyChord.Parse("Win+Shift+F1");
        Assert.True(c.Modifiers.HasFlag(HotkeyModifiers.Command));
    }
}
