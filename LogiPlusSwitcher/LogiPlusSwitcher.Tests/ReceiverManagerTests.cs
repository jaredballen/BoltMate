using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Tests.Support;
using Xunit;

namespace LogiPlusSwitcher.Tests;

public class ReceiverManagerTests
{
    [Fact]
    public void Initial_refresh_attaches_every_present_receiver()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/test/bolt-0", "SER-0");
        transport.AddReceiver("/test/bolt-1", "SER-1");

        var attached = new List<BoltReceiver>();
        using var mgr = new ReceiverManager(transport, autoStart: false);
        mgr.ReceiverAttached += (_, r) => attached.Add(r);

        mgr.Refresh();

        Assert.Equal(2, attached.Count);
        Assert.Equal(2, mgr.Receivers.Count);
        Assert.Equal(new[] { "SER-0", "SER-1" }, mgr.Receivers.Select(r => r.Info.Serial).OrderBy(s => s));
    }

    [Fact]
    public void Receiver_unplug_fires_detached_and_disposes()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/test/bolt-0", "SER-0");

        using var mgr = new ReceiverManager(transport, autoStart: false);
        mgr.Refresh();
        var initial = mgr.Receivers.Single();

        BoltReceiver? detached = null;
        mgr.ReceiverDetached += (_, r) => detached = r;

        transport.RemoveReceiver("/test/bolt-0");
        mgr.Refresh();

        Assert.NotNull(detached);
        Assert.Same(initial, detached);
        Assert.Empty(mgr.Receivers);
    }

    [Fact]
    public void Unplug_then_replug_yields_a_new_receiver_instance()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/test/bolt-0", "SER-0");

        using var mgr = new ReceiverManager(transport, autoStart: false);
        mgr.Refresh();
        var first = mgr.Receivers.Single();

        transport.RemoveReceiver("/test/bolt-0");
        mgr.Refresh();
        Assert.Empty(mgr.Receivers);

        transport.AddReceiver("/test/bolt-0", "SER-0");
        mgr.Refresh();

        var second = mgr.Receivers.Single();
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Attach_failure_surfaces_via_AttachFailed_event_without_killing_manager()
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
        mgr.AttachFailed += (_, ex) => observed = ex;

        mgr.Refresh();
        Assert.NotNull(observed);
        Assert.Empty(mgr.Receivers);

        // Next refresh should retry and succeed.
        mgr.Refresh();
        Assert.Single(mgr.Receivers);
    }

    [Fact]
    public void Idempotent_refresh_does_not_re_attach_existing_receivers()
    {
        var transport = new FakeReceiverTransport();
        transport.AddReceiver("/test/bolt-0", "SER-0");

        var attaches = 0;
        using var mgr = new ReceiverManager(transport, autoStart: false);
        mgr.ReceiverAttached += (_, _) => attaches++;

        mgr.Refresh();
        mgr.Refresh();
        mgr.Refresh();

        Assert.Equal(1, attaches);
    }
}
