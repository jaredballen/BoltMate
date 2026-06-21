using BoltMate.Core.Bolt;
using Xunit;

namespace BoltMate.Tests;

public class WpidCatalogTests
{
    [Theory]
    [InlineData(0xB034, "MX Master 3S")]
    [InlineData(0xB378, "MX Anywhere 3S")]
    [InlineData(0xB35E, "MX Keys S")]
    [InlineData(0xB33C, null)] // unknown
    public void Lookup_returns_known_model_or_null(ushort wpid, string? expected)
    {
        Assert.Equal(expected, WpidCatalog.Lookup(wpid));
    }

    [Fact]
    public void LookupOrFallback_returns_hex_placeholder_for_unknown()
    {
        Assert.Equal("Logi 0xFFFF", WpidCatalog.LookupOrFallback(0xFFFF));
    }

    [Fact]
    public void LookupOrFallback_returns_known_name()
    {
        Assert.Equal("MX Master 3S", WpidCatalog.LookupOrFallback(0xB034));
    }
}
