using DynamicData;
using BoltMate.Core.Bolt;
using BoltMate.Hid.Abstractions;
using BoltMate.Core.HidPp;
using BoltMate.Core.Switcher;
using BoltMate.Tests.Support;
using Xunit;

namespace BoltMate.Tests;

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

        public BoltReceiver AddReceiver(string path = "/test/bolt-0", string serial = "SER-0")
        {
            Transport.AddReceiver(path, serial);
            Manager.Refresh();
            return Manager.Receivers.Lookup(path).Value;
        }

        public PairedDevice SeedDevice(BoltReceiver receiver, byte slot, byte? changeHostIndex = 0x09, byte? reprogControlsIndex = 0x07, bool linkUp = true, params (byte hostIndex, string? hostName)[] bindings)
        {
            var device = receiver.EnsureSlot(slot);
            device.ChangeHostIndex = changeHostIndex;
            device.ReprogControlsIndex = reprogControlsIndex;
            device.LinkUp = linkUp;
            if (bindings.Length > 0)
            {
                var dict = new Dictionary<byte, HostBinding>();
                foreach (var (h, name) in bindings)
                    dict[h] = new HostBinding(h, Paired: name is not null, HostIdentifier: null, ReceiverName: name);
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

    private const string HostA = "machine-A";
    private const string HostB = "machine-B";
    private const string HostC = "machine-C";

    [Fact]
    public void HostSwitchPress_routes_each_sibling_to_slot_matching_host_name()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();

        // Mouse on slot 1 with bindings: host 0 -> machine-A, host 1 -> machine-B
        // Keyboard on slot 2: same shape
        // Headset on slot 3: machine-B lives on slot 2 (different slot for same host!)
        f.SeedDevice(receiver, 1, bindings: [(0, HostA), (1, HostB)]);
        f.SeedDevice(receiver, 2, bindings: [(0, HostA), (1, HostB)]);
        f.SeedDevice(receiver, 3, bindings: [(0, HostA), (2, HostB)]);

        // Mouse pressed Easy-Switch to host 1 (= machine-B)
        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 1);

        Assert.Equal(2, f.FanOuts.Count);
        Assert.Contains(f.FanOuts, e => e.Target.DeviceIndex == 2 && e.TargetHost == 1);
        Assert.Contains(f.FanOuts, e => e.Target.DeviceIndex == 3 && e.TargetHost == 2);
    }

    [Fact]
    public void HostSwitchPress_skips_sibling_with_no_binding_to_target_host()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();

        f.SeedDevice(receiver, 1, bindings: [(0, HostA), (1, HostB)]);
        // Keyboard knows A and C — no slot for B.
        f.SeedDevice(receiver, 2, bindings: [(0, HostA), (1, HostC)]);

        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 1);

        Assert.Empty(f.FanOuts);
    }

    [Fact]
    public void HostSwitchPress_with_no_origin_bindings_does_not_fan_out()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();

        // Origin has no HostBindings yet — fan-out can't resolve a target host
        // name, so nothing should switch. (Prior "same-index fallback" routing
        // is gone; hostnames are the only correlation key.)
        f.SeedDevice(receiver, 1);
        f.SeedDevice(receiver, 2);

        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 0);

        Assert.Empty(f.FanOuts);
    }

    [Fact]
    public void HostSwitchPress_excludes_origin_device()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();
        f.SeedDevice(receiver, 1, bindings: [(0, HostA), (1, HostB)]);
        f.SeedDevice(receiver, 2, bindings: [(0, HostA), (1, HostB)]);

        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 1);

        Assert.DoesNotContain(f.FanOuts, e => e.Target.DeviceIndex == 1);
    }

    [Fact]
    public void Cross_receiver_fan_out_works()
    {
        using var f = new Fixture();
        var receiverA = f.AddReceiver("/test/bolt-A", "SER-A");
        var receiverB = f.AddReceiver("/test/bolt-B", "SER-B");

        // Mouse on receiver A, keyboard on receiver B, both paired to machine-C
        // (some third host) at host-index 1.
        f.SeedDevice(receiverA, 1, bindings: [(0, HostA), (1, HostC)]);
        f.SeedDevice(receiverB, 2, bindings: [(0, HostB), (1, HostC)]);

        f.InjectHostSwitchPress(receiverA, originatingSlot: 1, targetHost: 1);

        Assert.Single(f.FanOuts);
        Assert.Equal((byte)2, f.FanOuts[0].Target.DeviceIndex);
        Assert.Equal((byte)1, f.FanOuts[0].TargetHost);
        Assert.Equal(receiverA, f.FanOuts[0].OriginatingReceiver);
    }

    [Fact]
    public void Offline_devices_are_skipped()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();
        f.SeedDevice(receiver, 1, bindings: [(0, HostA), (1, HostB)]);
        f.SeedDevice(receiver, 2, bindings: [(0, HostA), (1, HostB)], linkUp: false);
        f.SeedDevice(receiver, 3, bindings: [(0, HostA), (1, HostB)]);

        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 1);

        Assert.Single(f.FanOuts);
        Assert.Equal((byte)3, f.FanOuts[0].Target.DeviceIndex);
    }

    [Fact]
    public void Devices_without_ChangeHost_support_are_skipped()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();
        f.SeedDevice(receiver, 1, bindings: [(0, HostA), (1, HostB)]);
        f.SeedDevice(receiver, 2, bindings: [(0, HostA), (1, HostB)]);
        f.SeedDevice(receiver, 3, changeHostIndex: null, bindings: [(0, HostA), (1, HostB)]);

        f.InjectHostSwitchPress(receiver, originatingSlot: 1, targetHost: 1);

        Assert.Single(f.FanOuts);
        Assert.Equal((byte)2, f.FanOuts[0].Target.DeviceIndex);
    }

    [Fact]
    public void Release_event_does_not_fan_out()
    {
        using var f = new Fixture();
        var receiver = f.AddReceiver();
        f.SeedDevice(receiver, 1, bindings: [(0, HostA), (1, HostB)]);
        f.SeedDevice(receiver, 2, bindings: [(0, HostA), (1, HostB)]);

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
