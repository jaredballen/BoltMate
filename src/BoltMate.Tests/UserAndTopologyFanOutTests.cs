using BoltMate.Core.Services;
using DynamicData;
using BoltMate.Core.Bolt;
using BoltMate.Hid.Abstractions;
using BoltMate.Tests.Support;
using Xunit;

namespace BoltMate.Tests;

/// <summary>
/// Coverage for SwitcherService.RequestTopologyFanOut — the hostname-based
/// fan-out used by both user-requested switches (source=UserRequested, no
/// originator) and the UDP topology correlator (source=RemoteTopology,
/// originator = device that just left this machine).
/// </summary>
public class UserAndTopologyFanOutTests
{
    private sealed class Fixture : IDisposable
    {
        public FakeReceiverTransport Transport { get; } = new();
        public ReceiverManager Manager { get; }
        public SwitcherService Switcher { get; }
        public List<FanOutEvent> FanOuts { get; } = new();
        private readonly IDisposable _sub;

        public Fixture()
        {
            Manager = new ReceiverManager(Transport, autoStart: false);
            Switcher = new SwitcherService(Manager);
            _sub = Switcher.FanOuts.Subscribe(ev => FanOuts.Add(ev));
        }

        public BoltReceiver AddReceiver(string path = "/test/bolt-0", string serial = "SER-0")
        {
            Transport.AddReceiver(path, serial);
            Manager.Refresh();
            return Manager.Receivers.Lookup(path).Value;
        }

        public PairedDevice SeedDevice(BoltReceiver receiver, byte slot, ushort wpid = 0x1234, bool linkUp = true,
            params (byte hostIndex, string? hostName)[] bindings)
        {
            var device = receiver.EnsureSlot(slot);
            device.ChangeHostIndex = 0x09;
            device.ReprogControlsIndex = 0x07;
            device.LinkUp = linkUp;
            device.Wpid = wpid;
            if (bindings.Length > 0)
            {
                var dict = new Dictionary<byte, HostBinding>();
                foreach (var (h, name) in bindings)
                    dict[h] = new HostBinding(h, Paired: name is not null, HostIdentifier: null, ReceiverName: name);
                device.HostBindings = dict;
            }
            return device;
        }

        public void Dispose()
        {
            _sub.Dispose();
            Switcher.Dispose();
            Manager.Dispose();
        }
    }

    private const string HostA = "machine-A";
    private const string HostB = "machine-B";

    [Fact]
    public void UserTopologyFanOut_routes_each_device_via_its_own_matching_slot()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        // Mouse: slot 1 -> A, slot 2 -> B.
        f.SeedDevice(r, 1, wpid: 0xAAAA, bindings: [(1, HostA), (2, HostB)]);
        // Keyboard: slot 0 -> B (different slot index for the same host!).
        f.SeedDevice(r, 2, wpid: 0xBBBB, bindings: [(0, HostB), (1, HostA)]);

        var count = f.Switcher.RequestTopologyFanOut(
            HostB,
            originatingDeviceWpid: null,
            source: FanOutSource.UserRequested);

        Assert.Equal(2, count);
        Assert.Contains(f.FanOuts, e => e.Target.DeviceIndex == 1 && e.TargetHost == 2);
        Assert.Contains(f.FanOuts, e => e.Target.DeviceIndex == 2 && e.TargetHost == 0);
        Assert.All(f.FanOuts, e => Assert.Equal(FanOutSource.UserRequested, e.Source));
    }

    [Fact]
    public void UserTopologyFanOut_skips_device_without_matching_host_binding()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        // Mouse only knows host A — no slot maps to B.
        f.SeedDevice(r, 1, wpid: 0xAAAA, bindings: [(0, HostA), (1, HostA)]);
        // Keyboard slot 1 -> B.
        f.SeedDevice(r, 2, wpid: 0xBBBB, bindings: [(0, HostA), (1, HostB)]);

        var count = f.Switcher.RequestTopologyFanOut(
            HostB,
            originatingDeviceWpid: null,
            source: FanOutSource.UserRequested);

        Assert.Equal(1, count); // keyboard only
        Assert.DoesNotContain(f.FanOuts, e => e.Target.DeviceIndex == 1);
    }

    [Fact]
    public void UserTopologyFanOut_skips_offline_devices()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        f.SeedDevice(r, 1, wpid: 0xAAAA, linkUp: false, bindings: [(1, HostB)]);
        f.SeedDevice(r, 2, wpid: 0xBBBB, bindings: [(1, HostB)]);

        var count = f.Switcher.RequestTopologyFanOut(HostB, null, FanOutSource.UserRequested);

        Assert.Equal(1, count);
        Assert.DoesNotContain(f.FanOuts, e => e.Target.DeviceIndex == 1);
    }

    [Fact]
    public void TopologyFanOut_matches_local_sibling_with_binding_to_remote_host()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();

        // Local mouse + keyboard. Mouse just disconnected (it's the originator).
        // Keyboard has a binding to host B at host slot 1.
        f.SeedDevice(r, 1, wpid: 0xAAAA, linkUp: false); // mouse (just left)
        f.SeedDevice(r, 2, wpid: 0xBBBB, bindings: [(0, HostA), (1, HostB)]);

        var count = f.Switcher.RequestTopologyFanOut(HostB, originatingDeviceWpid: 0xAAAA);

        Assert.Equal(1, count);
        var ev = f.FanOuts.Single();
        Assert.Equal((byte)2, ev.Target.DeviceIndex);
        Assert.Equal((byte)1, ev.TargetHost);
        Assert.Equal(FanOutSource.RemoteTopology, ev.Source);
    }

    [Fact]
    public void TopologyFanOut_skips_originator_device_to_avoid_loop()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        // Originator still appears in the local cache (just went down very recently).
        f.SeedDevice(r, 1, wpid: 0xAAAA, bindings: [(0, HostA), (1, HostB)]);

        var count = f.Switcher.RequestTopologyFanOut(HostB, originatingDeviceWpid: 0xAAAA);

        Assert.Equal(0, count);
    }

    [Fact]
    public void TopologyFanOut_skips_devices_without_matching_host_binding()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        // Sibling only knows host A — no slot maps to B.
        f.SeedDevice(r, 1, wpid: 0xCCCC, bindings: [(0, HostA), (1, HostA)]);

        var count = f.Switcher.RequestTopologyFanOut(HostB, originatingDeviceWpid: 0xAAAA);

        Assert.Equal(0, count);
    }
}
