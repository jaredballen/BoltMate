using DynamicData;
using LogiPlusSwitcher.App;
using LogiPlusSwitcher.Core;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LogiPlusSwitcher.Tests;

public class ReceiverPolicyServiceTests
{
    private static ReceiverManager MakeManager(FakeReceiverTransport transport) =>
        new(transport, autoStart: false);

    [Fact]
    public void Pro_tier_marks_all_attached_as_participating()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/p1", "S1");
        transport.AddReceiver("/p2", "S2");

        using var manager = MakeManager(transport);
        var license = new StubLicenseService(initialPro: true);
        var settings = new AppSettings();
        using var policy = new ReceiverPolicyService(manager, license, settings, NullLogger<ReceiverPolicyService>.Instance);

        manager.Refresh();

        Assert.All(manager.Receivers.Items, r => Assert.True(r.IsParticipating));
    }

    [Fact]
    public void Free_with_single_receiver_participates_but_does_not_auto_persist_primary()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/p1", "S1");

        using var manager = MakeManager(transport);
        var license = new StubLicenseService(initialPro: false);
        var settings = new AppSettings();
        using var policy = new ReceiverPolicyService(manager, license, settings, NullLogger<ReceiverPolicyService>.Instance);

        manager.Refresh();

        Assert.True(manager.Receivers.Items.Single().IsParticipating);
        // Avoid auto-locking primary on transient single-receiver state.
        Assert.Null(settings.PrimaryReceiverSerial);
    }

    [Fact]
    public void Free_with_multiple_receivers_and_primary_set_only_participates_primary()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/p1", "S1");
        transport.AddReceiver("/p2", "S2");

        using var manager = MakeManager(transport);
        var license = new StubLicenseService(initialPro: false);
        var settings = new AppSettings { PrimaryReceiverSerial = "S2" };
        using var policy = new ReceiverPolicyService(manager, license, settings, NullLogger<ReceiverPolicyService>.Instance);

        manager.Refresh();

        var participating = manager.Receivers.Items.Where(r => r.IsParticipating).ToList();
        Assert.Single(participating);
        Assert.Equal("S2", participating[0].Info.Serial);
    }

    [Fact]
    public void Free_with_multiple_receivers_and_no_primary_parks_all_and_fires_prompt()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/p1", "S1");
        transport.AddReceiver("/p2", "S2");

        using var manager = MakeManager(transport);
        var license = new StubLicenseService(initialPro: false);
        var settings = new AppSettings();
        using var policy = new ReceiverPolicyService(manager, license, settings, NullLogger<ReceiverPolicyService>.Instance);

        var promptCount = 0;
        using var sub = policy.MultiReceiverPromptRequired.Subscribe(_ => promptCount++);

        manager.Refresh();

        Assert.All(manager.Receivers.Items, r => Assert.False(r.IsParticipating));
        Assert.Equal(1, promptCount);
    }

    [Fact]
    public void Upgrading_to_Pro_flips_all_to_participating()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/p1", "S1");
        transport.AddReceiver("/p2", "S2");

        using var manager = MakeManager(transport);
        var license = new StubLicenseService(initialPro: false);
        var settings = new AppSettings { PrimaryReceiverSerial = "S1" };
        using var policy = new ReceiverPolicyService(manager, license, settings, NullLogger<ReceiverPolicyService>.Instance);

        manager.Refresh();
        Assert.Equal(1, manager.Receivers.Items.Count(r => r.IsParticipating));

        license.SetPro(true);

        Assert.All(manager.Receivers.Items, r => Assert.True(r.IsParticipating));
    }
}
