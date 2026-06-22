using BoltMate.Core.Topology;
using Xunit;

namespace BoltMate.Tests;

/// <summary>
/// Static-state guard: <see cref="LocalHostIdentity"/> resolves OS-specific
/// names once at JIT time, so we can't test the resolver directly without
/// reflection. What we CAN test is the matching contract — and the
/// guarantee that at least one alias is always returned (the "unknown"
/// fallback if every resolver fails).
/// </summary>
public class LocalHostIdentityTests
{
    [Fact]
    public void Names_always_returns_at_least_one_entry()
    {
        Assert.NotEmpty(LocalHostIdentity.Names);
    }

    [Fact]
    public void Canonical_returns_first_alias()
    {
        Assert.Equal(LocalHostIdentity.Names[0], LocalHostIdentity.Canonical);
    }

    [Fact]
    public void Matches_returns_true_for_any_known_alias()
    {
        foreach (var name in LocalHostIdentity.Names)
            Assert.True(LocalHostIdentity.Matches(name), $"expected match for own alias '{name}'");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Matches_returns_false_for_empty_or_null(string? input)
    {
        Assert.False(LocalHostIdentity.Matches(input));
    }

    [Fact]
    public void Matches_is_case_insensitive_for_known_aliases()
    {
        // OS hostnames are case-insensitive on Windows + (mostly) macOS.
        var canon = LocalHostIdentity.Canonical;
        Assert.True(LocalHostIdentity.Matches(canon.ToUpperInvariant()));
        Assert.True(LocalHostIdentity.Matches(canon.ToLowerInvariant()));
    }

    [Fact]
    public void Matches_trims_whitespace_on_candidate()
    {
        var canon = LocalHostIdentity.Canonical;
        Assert.True(LocalHostIdentity.Matches($"  {canon}  "));
    }

    [Fact]
    public void Matches_returns_false_for_obvious_non_match()
    {
        Assert.False(LocalHostIdentity.Matches("definitely-not-this-machine-zzz"));
    }
}
