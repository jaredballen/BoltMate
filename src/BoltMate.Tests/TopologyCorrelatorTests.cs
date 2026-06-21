using System.Reactive.Subjects;
using DynamicData;
using BoltMate.Core.Bolt;
using BoltMate.Core.Switcher;
using BoltMate.Core.Topology;
using BoltMate.Hid.Abstractions;
using BoltMate.Tests.Support;
using Xunit;

namespace BoltMate.Tests;

/// <summary>
/// Exercises the link-lost -> remote-announcement correlation that drives
/// cross-machine fan-out. We feed synthetic <see cref="ReceiverAnnouncement"/>s
/// into the correlator via a Subject so the test never needs a real socket.
/// </summary>
public class TopologyCorrelatorTests
{
    private static byte[] BleA => [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];
    private static byte[] BleB => [0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5];

    private sealed class Fixture : IDisposable
    {
        public FakeReceiverTransport Transport { get; } = new();
        public ReceiverManager Manager { get; }
        public SwitcherService Switcher { get; }
        public List<FanOutEvent> FanOuts { get; } = new();
        public Subject<ReceiverAnnouncement> Announcements { get; } = new();
        public TopologyCorrelator Correlator { get; }
        private readonly IDisposable _sub;

        public Fixture(TimeSpan? window = null)
        {
            Manager = new ReceiverManager(Transport, autoStart: false);
            Switcher = new SwitcherService(Manager);
            _sub = Switcher.FanOuts.Subscribe(ev => FanOuts.Add(ev));
            Correlator = new TopologyCorrelator(Manager, Switcher, Announcements,
                window ?? TimeSpan.FromSeconds(3));
        }

        public BoltReceiver AddReceiver(string path = "/test/bolt-0", string serial = "SER-0")
        {
            Transport.AddReceiver(path, serial);
            Manager.Refresh();
            return Manager.Receivers.Lookup(path).Value;
        }

        public PairedDevice SeedDevice(BoltReceiver receiver, byte slot, ushort wpid,
            params (byte hostIndex, byte[]? ble, string? hostName)[] bindings)
        {
            var d = receiver.EnsureSlot(slot);
            d.ChangeHostIndex = 0x09;
            d.ReprogControlsIndex = 0x07;
            d.LinkUp = true;
            d.Wpid = wpid;
            if (bindings.Length > 0)
            {
                var dict = new Dictionary<byte, HostBinding>();
                foreach (var (h, ble, hostName) in bindings)
                    dict[h] = new HostBinding(h, ble is not null || hostName is not null, ble, hostName);
                d.HostBindings = dict;
            }
            return d;
        }

        public void Dispose()
        {
            _sub.Dispose();
            Correlator.Dispose();
            Announcements.Dispose();
            Switcher.Dispose();
            Manager.Dispose();
        }
    }

    [Fact]
    public void LinkLost_then_remote_announcement_with_matching_wpid_triggers_fan_out()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        // Mouse 0xAAAA — will be the originator that leaves us.
        var mouse = f.SeedDevice(r, 1, wpid: 0xAAAA, (0, BleA, "host-A"), (1, BleB, "remote-mac"));
        // Keyboard 0xBBBB on slot 1->BleB. Should follow when correlator fires.
        f.SeedDevice(r, 2, wpid: 0xBBBB, (0, BleA, "host-A"), (1, BleB, "remote-mac"));

        // Simulate link-lost for the mouse: use BoltReceiver's notification path.
        InjectLinkLost(f, r, mouse);

        // Now the remote machine emits an announcement saying mouse 0xAAAA is online there,
        // on a receiver whose BLE matches BleB.
        f.Announcements.OnNext(new ReceiverAnnouncement
        {
            MachineId = "remote",
            Hostname = "remote-mac",
            Receivers = new List<ReceiverAnnouncementEntry>
            {
                new()
                {
                    Serial = "REMOTE-SER",
                    HostIdentifierHex = Convert.ToHexString(BleB).ToLowerInvariant(),
                    OnlineDevices = new List<OnlineDeviceEntry>
                    {
                        new() { Slot = 1, WpidHex = "AAAA" }
                    }
                }
            }
        });

        Assert.Single(f.FanOuts);
        var ev = f.FanOuts[0];
        Assert.Equal((byte)2, ev.Target.DeviceIndex);     // keyboard
        Assert.Equal((byte)1, ev.TargetHost);             // its slot mapped to BleB
        Assert.Equal(FanOutSource.RemoteTopology, ev.Source);
    }

    [Fact]
    public void Announcement_without_matching_lost_wpid_is_ignored()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        f.SeedDevice(r, 2, wpid: 0xBBBB, (0, BleA, "host-A"), (1, BleB, "remote-mac"));

        f.Announcements.OnNext(new ReceiverAnnouncement
        {
            MachineId = "remote",
            Receivers = new List<ReceiverAnnouncementEntry>
            {
                new()
                {
                    HostIdentifierHex = Convert.ToHexString(BleB).ToLowerInvariant(),
                    OnlineDevices = new List<OnlineDeviceEntry>
                    {
                        new() { Slot = 1, WpidHex = "AAAA" }
                    }
                }
            }
        });

        Assert.Empty(f.FanOuts);
    }

    [Fact]
    public void LinkLost_expires_after_correlation_window()
    {
        using var f = new Fixture(window: TimeSpan.FromMilliseconds(50));
        var r = f.AddReceiver();
        var mouse = f.SeedDevice(r, 1, wpid: 0xAAAA, (1, BleB, "remote-mac"));
        f.SeedDevice(r, 2, wpid: 0xBBBB, (1, BleB, "remote-mac"));

        InjectLinkLost(f, r, mouse);
        Thread.Sleep(120); // window expires

        f.Announcements.OnNext(new ReceiverAnnouncement
        {
            MachineId = "remote",
            Receivers = new List<ReceiverAnnouncementEntry>
            {
                new()
                {
                    HostIdentifierHex = Convert.ToHexString(BleB).ToLowerInvariant(),
                    OnlineDevices = new List<OnlineDeviceEntry>
                    {
                        new() { Slot = 1, WpidHex = "AAAA" }
                    }
                }
            }
        });

        Assert.Empty(f.FanOuts);
    }

    /// <summary>
    /// Forces a link-lost event through BoltReceiver by injecting a DJ_PAIRING
    /// frame with the link-lost flag set. Mirrors how the real notification
    /// pump processes 0x41 frames.
    /// </summary>
    private void InjectLinkLost(Fixture f, BoltReceiver receiver, PairedDevice device)
    {
        var bytes = new byte[7];
        bytes[0] = 0x10;
        bytes[1] = device.DeviceIndex;
        bytes[2] = 0x41; // sub-id
        bytes[3] = 0x00;
        bytes[4] = 0x40; // link lost flag (bit 0x40)
        bytes[5] = (byte)(device.Wpid & 0xFF);
        bytes[6] = (byte)((device.Wpid >> 8) & 0xFF);
        var frame = HidPpFrame.TryParse(bytes)!.Value;

        var session = f.Transport.Sessions.First(s => s.Info.Path == receiver.Info.Path);
        session.LastConnection.Inject(frame);
    }
}
