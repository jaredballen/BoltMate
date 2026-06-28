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
        Assert.False(s.TelemetryEnabled);
        Assert.False(s.HasShownWelcome);
        Assert.True(s.AutoCheckForUpdates);
        Assert.NotNull(s.Topology);
    }

    [Fact]
    public void Round_trips_through_System_Text_Json()
    {
        var s = new AppSettings
        {
            TelemetryEnabled = true,
            HasShownWelcome = true,
            AutoCheckForUpdates = false,
        };
        s.Topology.Enabled = true;

        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.True(back.TelemetryEnabled);
        Assert.True(back.HasShownWelcome);
        Assert.False(back.AutoCheckForUpdates);
        Assert.True(back.Topology.Enabled);
    }
}
