using DynamicData;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Hid;
using LogiPlusSwitcher.Core.HidPp;
using LogiPlusSwitcher.Core.Switcher;
using LogiPlusSwitcher.Tests.Support;
using Xunit;

namespace LogiPlusSwitcher.Tests;

public class SwitcherServiceTests
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
            if (bleAddress is not null)
                receiver.BluetoothAddress = bleAddress;
            return receiver;
        }

        public PairedDevice SeedDevice(BoltReceiver receiver, byte slot, byte? changeHostIndex = 0x09, byte? reprogControlsIndex = 0x07, bool linkUp = true, params (byte hostIndex, byte[]? ble)[] bindings)
        {
            var device = receiver.EnsureSlot(slot);
            device.ChangeHostIndex = changeHostIndex;
            device.ReprogControlsIndex = reprogControlsIndex;
            device.LinkUp = linkUp;
            if (bindings.Length > 0)
            {
                var dict = new Dictionary<byte, HostBinding>();
                foreach (var (h, ble) in bindings)
                    dict[h] = new HostBinding(h, Paired: ble is not null, ble, null);
                device.HostBindings = dict;
            }
            return device;
        }

        public void InjectHostSwitchPress(BoltReceiver receiver, byte originatingSlot, byte targetHost)
        {
            var conn = Transport.Sessions.First(s => s.Info.Path == receiver.Info.Path).LastConnection;
            var device = receiver.TryGetDevice(originatingSlot)!;
            var buffer = new byte[HidPpConstants.LongReportLength];
            buffer[0] = 0x11;
            buffer[1] = originatingSlot;
            buffer[2] = device.ReprogControlsIndex!.Value;
            buffer[3] = 0x00;
            buffer[4] = 0x00;
            buffer[5] = (byte)(0xD1 + targetHost);
            conn.Inject(HidPpFrame.TryParse(buffer)!.Value);
        }

        public IReadOnlyList<HidPpFrame> WritesOf(BoltReceiver receiver)
        {
            var conn = Transport.Sessions.First(s => s.Info.Path == receiver.Info.Path).LastConnection;
            return conn.Writes.ToArray();
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
    private static byte[] BleC => [0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5];

    [Fact]
    public void HostSwitchPress_with_BLE_bindings_routes_each_sibling_to_its_matching_slot()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();

        // Mouse on slot 1 with bindings: host 0 -> BleA, host 1 -> BleB
        // Keyboard on slot 2 with bindings: host 0 -> BleA, host 1 -> BleB
        // Headset on slot 3 with bindings: host 0 -> BleA, host 2 -> BleB (different slot for B!)
        f.SeedDevice(receiver, 1, bindings: [(0, BleA), (1, BleB)]);
        f.SeedDevice(receiver, 2, bindings: [(0, BleA), (1, BleB)]);
        f.SeedDevice(receiver, 3, bindings: [(0, BleA), (2, BleB)]);

        // Mouse pressed Easy-Switch to host 1 (BLE B)
        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 1);

        Assert.Equal(2, f.FanOuts.Count);

        // Keyboard slot 2 should get CHANGE_HOST(1) — its slot 1 points to BLE B
        Assert.Contains(f.FanOuts, e => e.Target.DeviceIndex == 2 && e.TargetHost == 1);
        // Headset slot 3 should get CHANGE_HOST(2) — its slot 2 points to BLE B
        Assert.Contains(f.FanOuts, e => e.Target.DeviceIndex == 3 && e.TargetHost == 2);
    }

    [Fact]
    public void HostSwitchPress_skips_sibling_with_no_binding_to_target_BLE()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();

        // Mouse host 1 -> BleB
        f.SeedDevice(receiver, 1, bindings: [(0, BleA), (1, BleB)]);
        // Keyboard has bindings to A and C only — no slot maps to B.
        f.SeedDevice(receiver, 2, bindings: [(0, BleA), (1, BleC)]);

        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 1);

        Assert.Empty(f.FanOuts);
    }

    [Fact]
    public void HostSwitchPress_falls_back_to_legacy_same_index_when_origin_has_no_bindings()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();

        // Origin has no HostBindings (no DeviceEnricher pass yet).
        f.SeedDevice(receiver, 1);
        f.SeedDevice(receiver, 2);

        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 0);

        Assert.Single(f.FanOuts);
        Assert.Equal((byte)2, f.FanOuts[0].Target.DeviceIndex);
        Assert.Equal((byte)0, f.FanOuts[0].TargetHost);
    }

    [Fact]
    public void HostSwitchPress_excludes_origin_device()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();
        f.SeedDevice(receiver, 1, bindings: [(0, BleA), (1, BleB)]);
        f.SeedDevice(receiver, 2, bindings: [(0, BleA), (1, BleB)]);

        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 1);

        Assert.DoesNotContain(f.FanOuts, e => e.Target.DeviceIndex == 1);
    }

    [Fact]
    public void Cross_receiver_fan_out_works()
    {
        using var f = new Fixture();
        var receiverA = f.AddReceiver("/test/bolt-A", "SER-A");
        var receiverB = f.AddReceiver("/test/bolt-B", "SER-B");

        // Mouse on receiver A, keyboard on receiver B, both paired to BLE C (some third host).
        f.SeedDevice(receiverA, 1, bindings: [(0, BleA), (1, BleC)]);
        f.SeedDevice(receiverB, 2, bindings: [(0, BleB), (1, BleC)]);

        f.InjectHostSwitchPress(receiverA, originatingSlot: 1, targetHost: 1);

        Assert.Single(f.FanOuts);
        Assert.Equal((byte)2, f.FanOuts[0].Target.DeviceIndex);
        Assert.Equal((byte)1, f.FanOuts[0].TargetHost);
        Assert.Equal(receiverA, f.FanOuts[0].OriginatingReceiver);
    }

    [Fact]
    public void Non_participating_origin_is_ignored()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();
        receiver.IsParticipating = false;

        f.SeedDevice(receiver, 1, bindings: [(0, BleA), (1, BleB)]);
        f.SeedDevice(receiver, 2, bindings: [(0, BleA), (1, BleB)]);

        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 1);

        Assert.Empty(f.FanOuts);
    }

    [Fact]
    public void Non_participating_sibling_receiver_is_skipped()
    {
        using var f = new Fixture();
        var receiverA = f.AddReceiver("/test/bolt-A", "SER-A");
        var receiverB = f.AddReceiver("/test/bolt-B", "SER-B");
        receiverB.IsParticipating = false; // Free tier — secondary excluded.

        f.SeedDevice(receiverA, 1, bindings: [(0, BleA), (1, BleC)]);
        f.SeedDevice(receiverB, 2, bindings: [(0, BleB), (1, BleC)]);

        f.InjectHostSwitchPress(receiverA, originatingSlot: 1, targetHost: 1);

        Assert.Empty(f.FanOuts);
    }

    [Fact]
    public void Offline_devices_are_skipped()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();
        f.SeedDevice(receiver, 1, bindings: [(0, BleA), (1, BleB)]);
        f.SeedDevice(receiver, 2, bindings: [(0, BleA), (1, BleB)], linkUp: false);
        f.SeedDevice(receiver, 3, bindings: [(0, BleA), (1, BleB)]);

        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 1);

        Assert.Single(f.FanOuts);
        Assert.Equal((byte)3, f.FanOuts[0].Target.DeviceIndex);
    }

    [Fact]
    public void Devices_without_ChangeHost_support_are_skipped()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();
        f.SeedDevice(receiver, 1, bindings: [(0, BleA), (1, BleB)]);
        f.SeedDevice(receiver, 2, bindings: [(0, BleA), (1, BleB)]);
        f.SeedDevice(receiver, 3, changeHostIndex: null, bindings: [(0, BleA), (1, BleB)]);

        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 1);

        Assert.Single(f.FanOuts);
        Assert.Equal((byte)2, f.FanOuts[0].Target.DeviceIndex);
    }

    [Fact]
    public void Release_event_does_not_fan_out()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();
        f.SeedDevice(receiver, 1, bindings: [(0, BleA), (1, BleB)]);
        f.SeedDevice(receiver, 2, bindings: [(0, BleA), (1, BleB)]);

        var conn = f.Transport.Sessions.Single().LastConnection;
        var buffer = new byte[HidPpConstants.LongReportLength];
        buffer[0] = 0x11;
        buffer[1] = 0x01;
        buffer[2] = 0x07;
        buffer[3] = 0x00;
        // All CID slots zero = release
        conn.Inject(HidPpFrame.TryParse(buffer)!.Value);

        Assert.Empty(f.FanOuts);
    }
}
