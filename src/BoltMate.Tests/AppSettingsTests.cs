using System.Text.Json;
using BoltMate.Core;
using Xunit;

namespace BoltMate.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Defaults_are_sensible()
    {
        var s = new AppSettings();
        Assert.Equal(1, s.Version);
        Assert.Equal(3, s.HostNames.Length);
        Assert.False(s.TelemetryEnabled);
        Assert.Empty(s.Receivers);
    }

    [Fact]
    public void Round_trips_through_System_Text_Json()
    {
        var s = new AppSettings
        {
            HostNames = ["Work Mac", "Win Box", "Linux Desktop"],
            TelemetryEnabled = true,
        };
        s.Receivers["CEB26A"] = new ReceiverSettings
        {
            Nickname = "Desk Bolt",
            HostNames = ["a", "b", "c"],
        };

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(s.HostNames, back.HostNames);
        Assert.True(back.TelemetryEnabled);
        Assert.Single(back.Receivers);
        Assert.Equal("Desk Bolt", back.Receivers["CEB26A"].Nickname);
        Assert.Equal(new[] { "a", "b", "c" }, back.Receivers["CEB26A"].HostNames);
    }
}
