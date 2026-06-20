using DynamicData;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Hid.Abstractions;
using LogiPlusSwitcher.Core.Switcher;
using LogiPlusSwitcher.Tests.Support;
using Xunit;

namespace LogiPlusSwitcher.Tests;

/// <summary>
/// Coverage for SwitcherService.RequestTopologyFanOut — the address-based
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

        public BoltReceiver AddReceiver(string path = "/test/bolt-0", string serial = "SER-0", byte[]? bleAddress = null)
        {
            Transport.AddReceiver(path, serial);
            Manager.Refresh();
            var receiver = Manager.Receivers.Lookup(path).Value;
            if (bleAddress is not null) receiver.HostIdentifier = bleAddress;
            return receiver;
        }

        public PairedDevice SeedDevice(BoltReceiver receiver, byte slot, ushort wpid = 0x1234, bool linkUp = true,
            params (byte hostIndex, byte[]? ble)[] bindings)
        {
            var device = receiver.EnsureSlot(slot);
            device.ChangeHostIndex = 0x09;
            device.ReprogControlsIndex = 0x07;
            device.LinkUp = linkUp;
            device.Wpid = wpid;
            if (bindings.Length > 0)
            {
                var dict = new Dictionary<byte, HostBinding>();
                foreach (var (h, ble) in bindings)
                    dict[h] = new HostBinding(h, ble is not null, ble, null);
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

    private static byte[] BleA => [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];
    private static byte[] BleB => [0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5];

    [Fact]
    public void UserTopologyFanOut_routes_each_device_via_its_own_matching_slot()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        // Mouse: slot 1 maps to BleA, slot 2 maps to BleB.
        f.SeedDevice(r, 1, wpid: 0xAAAA, bindings: [(1, BleA), (2, BleB)]);
        // Keyboard: slot 0 maps to BleB (different slot index!).
        f.SeedDevice(r, 2, wpid: 0xBBBB, bindings: [(0, BleB), (1, BleA)]);

        var targetHostId = Convert.ToHexString(BleB).ToLowerInvariant();
        var count = f.Switcher.RequestTopologyFanOut(
            targetHostId,
            originatingDeviceWpid: null,
            source: FanOutSource.UserRequested);

        Assert.Equal(2, count);
        // Mouse should be routed to its slot 2 (its binding to BleB).
        Assert.Contains(f.FanOuts, e => e.Target.DeviceIndex == 1 && e.TargetHost == 2);
        // Keyboard should be routed to its slot 0 (its binding to BleB).
        Assert.Contains(f.FanOuts, e => e.Target.DeviceIndex == 2 && e.TargetHost == 0);
        Assert.All(f.FanOuts, e => Assert.Equal(FanOutSource.UserRequested, e.Source));
    }

    [Fact]
    public void UserTopologyFanOut_skips_device_without_matching_BLE_binding()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        // Mouse has bindings to BleA only — no slot maps to BleB.
        f.SeedDevice(r, 1, wpid: 0xAAAA, bindings: [(0, BleA), (1, BleA)]);
        // Keyboard slot 1 maps to BleB.
        f.SeedDevice(r, 2, wpid: 0xBBBB, bindings: [(0, BleA), (1, BleB)]);

        var targetHostId = Convert.ToHexString(BleB).ToLowerInvariant();
        var count = f.Switcher.RequestTopologyFanOut(
            targetHostId,
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
        f.SeedDevice(r, 1, wpid: 0xAAAA, linkUp: false, bindings: [(1, BleB)]);
        f.SeedDevice(r, 2, wpid: 0xBBBB, bindings: [(1, BleB)]);

        var targetHostId = Convert.ToHexString(BleB).ToLowerInvariant();
        var count = f.Switcher.RequestTopologyFanOut(targetHostId, null, FanOutSource.UserRequested);

        Assert.Equal(1, count);
        Assert.DoesNotContain(f.FanOuts, e => e.Target.DeviceIndex == 1);
    }

    [Fact]
    public void TopologyFanOut_matches_local_sibling_with_binding_to_remote_BLE()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();

        // Local mouse + keyboard. Mouse just disconnected (it's the originator).
        // Keyboard has a binding to remote BLE B at host slot 1.
        f.SeedDevice(r, 1, wpid: 0xAAAA, linkUp: false); // mouse (just left)
        f.SeedDevice(r, 2, wpid: 0xBBBB, bindings: [(0, BleA), (1, BleB)]);

        // Remote machine reports a receiver with BLE B and our mouse (0xAAAA) just came online there.
        var remoteBleKey = Convert.ToHexString(BleB).ToLowerInvariant();
        var count = f.Switcher.RequestTopologyFanOut(remoteBleKey, originatingDeviceWpid: 0xAAAA);

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
        f.SeedDevice(r, 1, wpid: 0xAAAA, bindings: [(0, BleA), (1, BleB)]);

        var remoteBleKey = Convert.ToHexString(BleB).ToLowerInvariant();
        var count = f.Switcher.RequestTopologyFanOut(remoteBleKey, originatingDeviceWpid: 0xAAAA);

        Assert.Equal(0, count);
    }

    [Fact]
    public void TopologyFanOut_skips_devices_without_matching_BLE_binding()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        // Sibling only has bindings to BLE A — no slot maps to remote B.
        f.SeedDevice(r, 1, wpid: 0xCCCC, bindings: [(0, BleA), (1, BleA)]);

        var remoteBleKey = Convert.ToHexString(BleB).ToLowerInvariant();
        var count = f.Switcher.RequestTopologyFanOut(remoteBleKey, originatingDeviceWpid: 0xAAAA);

        Assert.Equal(0, count);
    }
}
