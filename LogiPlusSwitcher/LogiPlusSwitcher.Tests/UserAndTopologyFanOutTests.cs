using DynamicData;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Hid.Abstractions;
using LogiPlusSwitcher.Core.Switcher;
using LogiPlusSwitcher.Tests.Support;
using Xunit;

namespace LogiPlusSwitcher.Tests;

/// <summary>
/// Coverage for SwitcherService's non-detection-driven fan-out paths:
/// RequestUserFanOut (hotkey) + RequestTopologyFanOut (UDP correlator).
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
            if (bleAddress is not null) receiver.BluetoothAddress = bleAddress;
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
    public void UserFanOut_writes_target_host_to_every_online_device()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        f.SeedDevice(r, 1, wpid: 0xAAAA);
        f.SeedDevice(r, 2, wpid: 0xBBBB);
        f.SeedDevice(r, 3, wpid: 0xCCCC, linkUp: false); // offline

        var count = f.Switcher.RequestUserFanOut(targetHost: 2);

        Assert.Equal(2, count);
        Assert.All(f.FanOuts, e => Assert.Equal((byte)2, e.TargetHost));
        Assert.All(f.FanOuts, e => Assert.Equal(FanOutSource.UserHotkey, e.Source));
        Assert.DoesNotContain(f.FanOuts, e => e.Target.DeviceIndex == 3);
    }

    [Fact]
    public void UserFanOut_skips_non_participating_receiver()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        r.IsParticipating = false;
        f.SeedDevice(r, 1);

        var count = f.Switcher.RequestUserFanOut(targetHost: 1);
        Assert.Equal(0, count);
        Assert.Empty(f.FanOuts);
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
