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
    private static BoltReceiverInfo Info() =>
        new("/test/path", "ABC123", "Logitech Bolt Receiver", "Logitech", 0x0001);

    private sealed class Fixture : IDisposable
    {
        public FakeReceiverConnection Conn { get; } = new();
        public BoltReceiver Receiver { get; }
        public SwitcherService Switcher { get; }
        public List<FanOutEvent> FanOuts { get; } = new();
        private readonly IDisposable _sub;

        public Fixture()
        {
            Receiver = new BoltReceiver(Info(), Conn);
            Switcher = new SwitcherService(Receiver);
            _sub = Switcher.FanOuts.Subscribe(ev => FanOuts.Add(ev));
        }

        public PairedDevice AddDevice(byte slot, byte? changeHostIndex = 0x09, byte? reprogControlsIndex = 0x07, bool linkUp = true)
        {
            var device = Receiver.EnsureSlot(slot);
            device.ChangeHostIndex = changeHostIndex;
            device.ReprogControlsIndex = reprogControlsIndex;
            device.LinkUp = linkUp;
            return device;
        }

        public void InjectHostSwitchPress(byte originatingSlot, byte targetHost)
        {
            var device = Receiver.TryGetDevice(originatingSlot)!;
            var buffer = new byte[HidPpConstants.LongReportLength];
            buffer[0] = 0x11;
            buffer[1] = originatingSlot;
            buffer[2] = device.ReprogControlsIndex!.Value;
            buffer[3] = 0x00;
            buffer[4] = 0x00;
            buffer[5] = (byte)(0xD1 + targetHost); // 0xD1=host0, 0xD2=host1, 0xD3=host2
            Conn.Inject(HidPpFrame.TryParse(buffer)!.Value);
        }

        public void InjectFlowSnoop(byte originatingSlot, byte targetHost, byte foreignSwId = 0x01)
        {
            var device = Receiver.TryGetDevice(originatingSlot)!;
            var buffer = new byte[HidPpConstants.LongReportLength];
            buffer[0] = 0x11;
            buffer[1] = originatingSlot;
            buffer[2] = device.ChangeHostIndex!.Value;
            buffer[3] = (byte)((1 << 4) | foreignSwId);
            buffer[4] = targetHost;
            Conn.Inject(HidPpFrame.TryParse(buffer)!.Value);
        }

        public void Dispose()
        {
            _sub.Dispose();
            Switcher.Dispose();
            Receiver.Dispose();
        }
    }

    [Fact]
    public void Easy_switch_press_fans_out_to_other_devices()
    {
        using var f = new Fixture();
        f.AddDevice(slot: 1); // keyboard (originator)
        f.AddDevice(slot: 2); // mouse
        f.AddDevice(slot: 3); // headset

        f.InjectHostSwitchPress(originatingSlot: 1, targetHost: 1);

        Assert.Equal(2, f.FanOuts.Count);
        Assert.Contains(f.FanOuts, e => e.Target.DeviceIndex == 2 && e.TargetHost == 1);
        Assert.Contains(f.FanOuts, e => e.Target.DeviceIndex == 3 && e.TargetHost == 1);
        Assert.All(f.FanOuts, e => Assert.Equal(FanOutSource.EasySwitchPress, e.Source));
        Assert.All(f.FanOuts, e => Assert.Equal((byte)1, e.OriginatingSlot));
    }

    [Fact]
    public void Originator_is_excluded_from_fanout()
    {
        using var f = new Fixture();
        f.AddDevice(slot: 1);
        f.AddDevice(slot: 2);

        f.InjectHostSwitchPress(originatingSlot: 1, targetHost: 0);

        Assert.Single(f.FanOuts);
        Assert.Equal((byte)2, f.FanOuts[0].Target.DeviceIndex);
    }

    [Fact]
    public void Offline_devices_are_skipped()
    {
        using var f = new Fixture();
        f.AddDevice(slot: 1);
        f.AddDevice(slot: 2);
        f.AddDevice(slot: 3, linkUp: false);

        f.InjectHostSwitchPress(originatingSlot: 1, targetHost: 2);

        Assert.Single(f.FanOuts);
        Assert.Equal((byte)2, f.FanOuts[0].Target.DeviceIndex);
    }

    [Fact]
    public void Devices_without_ChangeHost_support_are_skipped()
    {
        using var f = new Fixture();
        f.AddDevice(slot: 1);
        f.AddDevice(slot: 2);
        f.AddDevice(slot: 3, changeHostIndex: null);

        f.InjectHostSwitchPress(originatingSlot: 1, targetHost: 0);

        Assert.Single(f.FanOuts);
        Assert.Equal((byte)2, f.FanOuts[0].Target.DeviceIndex);
    }

    [Fact]
    public void Release_event_does_not_fan_out()
    {
        using var f = new Fixture();
        f.AddDevice(slot: 1);
        f.AddDevice(slot: 2);

        // All-zero CID payload = release.
        var buffer = new byte[HidPpConstants.LongReportLength];
        buffer[0] = 0x11;
        buffer[1] = 0x01;
        buffer[2] = 0x07;
        buffer[3] = 0x00;
        f.Conn.Inject(HidPpFrame.TryParse(buffer)!.Value);

        Assert.Empty(f.FanOuts);
    }

    [Fact]
    public void Flow_snoop_fans_out_to_other_devices()
    {
        using var f = new Fixture();
        f.AddDevice(slot: 1); // keyboard
        f.AddDevice(slot: 2); // mouse (originator — Logi+ wrote to it)

        f.InjectFlowSnoop(originatingSlot: 2, targetHost: 1, foreignSwId: 0x01);

        Assert.Single(f.FanOuts);
        Assert.Equal((byte)1, f.FanOuts[0].Target.DeviceIndex);
        Assert.Equal(FanOutSource.FlowSnoop, f.FanOuts[0].Source);
        Assert.Equal((byte)2, f.FanOuts[0].OriginatingSlot);
    }

    [Fact]
    public void Flow_snoop_with_our_sw_id_is_filtered_out()
    {
        using var f = new Fixture();
        f.AddDevice(slot: 1);
        f.AddDevice(slot: 2);

        // Our own write (sw_id == OurSwId) must NOT fire FlowHostSwitchDetected.
        f.InjectFlowSnoop(originatingSlot: 2, targetHost: 0, foreignSwId: HidPpConstants.OurSwId);

        Assert.Empty(f.FanOuts);
    }
}
