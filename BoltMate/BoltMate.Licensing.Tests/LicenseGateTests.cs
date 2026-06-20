using System;
using System.Threading.Tasks;
using BoltMate.Licensing;
using BoltMate.Licensing.Activation;
using BoltMate.Licensing.Configuration;
using BoltMate.Licensing.Contracts;
using BoltMate.Licensing.Crypto;
using BoltMate.Licensing.Storage;

namespace BoltMate.Licensing.Tests;

public sealed class LicenseGateTests
{
    private const string Issuer = "https://test-issuer.example.com";

    [Fact]
    public async Task Activate_stores_jwt_and_reports_valid()
    {
        var (gate, fakes) = BuildGate();
        fakes.Issue(daysFromNow: 30);

        var status = await gate.ActivateAsync();

        Assert.Equal(LicenseState.Valid, status.State);
        Assert.Equal(LicenseTier.Pro, status.Tier);
        Assert.Equal("user@example.com", status.Email);
        Assert.True(status.IsEntitled);
    }

    [Fact]
    public async Task Load_with_no_stored_token_is_NotActivated()
    {
        var (gate, _) = BuildGate();

        var status = await gate.LoadAsync();

        Assert.Equal(LicenseState.NotActivated, status.State);
        Assert.False(status.IsEntitled);
    }

    [Fact]
    public async Task Expired_token_within_grace_reports_GracePeriod()
    {
        var (gate, fakes) = BuildGate();
        fakes.Issue(daysFromNow: 30);
        await gate.ActivateAsync();

        fakes.Clock.UtcNow = fakes.Clock.UtcNow.AddDays(33);

        var status = await gate.LoadAsync();

        Assert.Equal(LicenseState.GracePeriod, status.State);
        Assert.True(status.IsEntitled);
    }

    [Fact]
    public async Task Expired_beyond_grace_reports_Expired()
    {
        var (gate, fakes) = BuildGate();
        fakes.Issue(daysFromNow: 30);
        await gate.ActivateAsync();

        fakes.Clock.UtcNow = fakes.Clock.UtcNow.AddDays(45);

        var status = await gate.LoadAsync();

        Assert.Equal(LicenseState.Expired, status.State);
        Assert.False(status.IsEntitled);
    }

    [Fact]
    public async Task Refresh_when_revoked_clears_token()
    {
        var (gate, fakes) = BuildGate();
        fakes.Issue(daysFromNow: 30);
        await gate.ActivateAsync();

        fakes.Clock.UtcNow = fakes.Clock.UtcNow.AddDays(28);
        fakes.Entitlements.ThrowOnRequest = new EntitlementRequestException("license_revoked", null, null);

        var status = await gate.RefreshAsync();

        Assert.Equal(LicenseState.Revoked, status.State);
        Assert.False(status.IsEntitled);
    }

    [Fact]
    public async Task Refresh_when_rate_limited_keeps_current_status()
    {
        var (gate, fakes) = BuildGate();
        fakes.Issue(daysFromNow: 30);
        await gate.ActivateAsync();

        fakes.Clock.UtcNow = fakes.Clock.UtcNow.AddDays(28);
        fakes.Entitlements.ThrowOnRequest = new EntitlementRequestException("rate_limited", null, 3600);

        var status = await gate.RefreshAsync();

        Assert.Equal(LicenseState.Valid, status.State);
        Assert.NotNull(status.RefreshFailedSince);
    }

    [Fact]
    public async Task SignOut_clears_status()
    {
        var (gate, fakes) = BuildGate();
        fakes.Issue(daysFromNow: 30);
        await gate.ActivateAsync();

        await gate.SignOutAsync();
        var status = await gate.LoadAsync();

        Assert.Equal(LicenseState.NotActivated, status.State);
    }

    private static (LicenseGate gate, Fakes fakes) BuildGate()
    {
        var keys = new TestKeys();
        var clock = new TestClock();
        var store = new InMemorySecureStore();
        var verifier = new JwtVerifier(keys.PublicKeyPem, Issuer);
        var auth = new FakeAuthFlow();
        var entitlements = new FakeEntitlementClient();
        var options = new LicensingOptions
        {
            Issuer = Issuer,
            PublicKeyPem = keys.PublicKeyPem,
            GracePeriod = TimeSpan.FromDays(7),
            RefreshBeforeExpiry = TimeSpan.FromDays(3)
        };

        var fakes = new Fakes(keys, clock, store, auth, entitlements);

        var gate = new LicenseGate(store, verifier, auth, entitlements, clock, options);
        return (gate, fakes);
    }

    private sealed class Fakes
    {
        public Fakes(TestKeys keys, TestClock clock, InMemorySecureStore store, FakeAuthFlow auth, FakeEntitlementClient entitlements)
        {
            Keys = keys;
            Clock = clock;
            Store = store;
            Auth = auth;
            Entitlements = entitlements;
        }

        public TestKeys Keys { get; }
        public TestClock Clock { get; }
        public InMemorySecureStore Store { get; }
        public FakeAuthFlow Auth { get; }
        public FakeEntitlementClient Entitlements { get; }

        public void Issue(int daysFromNow)
        {
            var iat = Clock.UtcNow;
            var exp = iat.AddDays(daysFromNow);
            Entitlements.OnRequest = _ => new EntitlementResponse(JwtVerifierTests.MintJwt(Keys, iat, exp), null);
        }
    }
}
