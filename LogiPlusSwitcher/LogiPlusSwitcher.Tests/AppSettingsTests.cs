using System.Text.Json;
using LogiPlusSwitcher.Core;
using Xunit;

namespace LogiPlusSwitcher.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Defaults_are_sensible()
    {
        var s = new AppSettings();
        Assert.Equal(1, s.Version);
        Assert.Equal(3, s.HostNames.Length);
        Assert.False(s.TelemetryEnabled);
        Assert.Null(s.LicenseKey);
        Assert.Empty(s.Receivers);
    }

    [Fact]
    public void Round_trips_through_System_Text_Json()
    {
        var s = new AppSettings
        {
            HostNames = ["Work Mac", "Win Box", "Linux Desktop"],
            LicenseKey = "ABC-123",
            TelemetryEnabled = true,
        };
        s.Receivers["CEB26A"] = new ReceiverSettings
        {
            Nickname = "Desk Bolt",
            HostNames = ["a", "b", "c"],
            ParticipatingSlots = [1, 3, 5],
        };

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(s.HostNames, back.HostNames);
        Assert.Equal("ABC-123", back.LicenseKey);
        Assert.True(back.TelemetryEnabled);
        Assert.Single(back.Receivers);
        Assert.Equal("Desk Bolt", back.Receivers["CEB26A"].Nickname);
        Assert.Equal(new byte[] { 1, 3, 5 }, back.Receivers["CEB26A"].ParticipatingSlots);
    }
}
