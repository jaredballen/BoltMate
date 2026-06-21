using DynamicData;
using BoltMate.Core.Bolt;
using BoltMate.Tests.Support;
using Xunit;

namespace BoltMate.Tests;

public class ReceiverManagerTests
{
    [Fact]
    public void Initial_refresh_attaches_every_present_receiver()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/test/bolt-0", "SER-0");
        transport.AddReceiver("/test/bolt-1", "SER-1");

        using var mgr = new ReceiverManager(transport, autoStart: false);
        mgr.Refresh();

        Assert.Equal(2, mgr.Receivers.Count);
        var serials = mgr.Receivers.Items.Select(r => r.Info.Serial).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "SER-0", "SER-1" }, serials);
    }

    [Fact]
    public void Receiver_unplug_removes_from_cache_and_disposes()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/test/bolt-0", "SER-0");

        using var mgr = new ReceiverManager(transport, autoStart: false);
        mgr.Refresh();
        var initial = mgr.Receivers.Items.Single();

        transport.RemoveReceiver("/test/bolt-0");
        mgr.Refresh();

        Assert.Empty(mgr.Receivers.Items);
    }

    [Fact]
    public void Unplug_then_replug_yields_a_new_receiver_instance()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/test/bolt-0", "SER-0");

        using var mgr = new ReceiverManager(transport, autoStart: false);
        mgr.Refresh();
        var first = mgr.Receivers.Items.Single();

        transport.RemoveReceiver("/test/bolt-0");
        mgr.Refresh();
        Assert.Empty(mgr.Receivers.Items);

        transport.AddReceiver("/test/bolt-0", "SER-0");
        mgr.Refresh();

        var second = mgr.Receivers.Items.Single();
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Attach_failure_surfaces_via_AttachFailures_stream_without_killing_manager()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/test/bolt-0", "SER-0");

        Exception? observed = null;
        var calls = 0;

        using var mgr = new ReceiverManager(
            transport,
            autoStart: false,
            receiverFactory: (info, conn) =>
            {
                calls++;
                if (calls == 1)
                    throw new InvalidOperationException("simulated open failure");
                return new BoltReceiver(info, conn);
            });
        using var sub = mgr.AttachFailures.Subscribe(ex => observed = ex);

        mgr.Refresh();
        Assert.NotNull(observed);
        Assert.Empty(mgr.Receivers.Items);

        // Next refresh should retry and succeed.
        mgr.Refresh();
        Assert.Single(mgr.Receivers.Items);
    }

    [Fact]
    public void Idempotent_refresh_does_not_re_attach_existing_receivers()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/test/bolt-0", "SER-0");

        var adds = 0;
        using var mgr = new ReceiverManager(transport, autoStart: false);
        using var sub = mgr.Receivers.Connect()
            .Subscribe(changes =>
            {
                foreach (var c in changes)
                {
                    if (c.Reason == ChangeReason.Add) adds++;
                }
            });

        mgr.Refresh();
        mgr.Refresh();
        mgr.Refresh();

        Assert.Equal(1, adds);
    }
}
