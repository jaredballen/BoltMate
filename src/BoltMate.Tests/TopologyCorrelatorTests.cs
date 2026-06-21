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
/// Coverage for the topology correlator's prune filter and the switch-event
/// driven cross-machine fan-out.
/// </summary>
public class TopologyCorrelatorTests
{
    private const string LocalHost = "local-mac";
    private const string PeerHost = "peer-pc";

    private sealed class Fixture : IDisposable
    {
        public FakeReceiverTransport Transport { get; } = new();
        public ReceiverManager Manager { get; }
        public SwitcherService Switcher { get; }
        public List<FanOutEvent> FanOuts { get; } = new();
        public Subject<ReceiverAnnouncement> Announcements { get; } = new();
        public TopologyCorrelator Correlator { get; }
        public List<ReceiverAnnouncement> Filtered { get; } = new();
        private readonly IDisposable _sub;
        private readonly IDisposable _filteredSub;

        public Fixture(string localHost = LocalHost)
        {
            Manager = new ReceiverManager(Transport, autoStart: false);
            Switcher = new SwitcherService(Manager);
            _sub = Switcher.FanOuts.Subscribe(ev => FanOuts.Add(ev));
            Correlator = new TopologyCorrelator(Manager, Switcher, Announcements, localHost);
            _filteredSub = Correlator.FilteredAnnouncements.Subscribe(a => Filtered.Add(a));
        }

        public BoltReceiver AddReceiver(string path = "/test/bolt-0", string serial = "SER-0")
        {
            Transport.AddReceiver(path, serial);
            Manager.Refresh();
            return Manager.Receivers.Lookup(path).Value;
        }

        public PairedDevice SeedDevice(BoltReceiver receiver, byte slot, ushort wpid,
            params (byte hostIndex, string? hostName)[] bindings)
        {
            var d = receiver.EnsureSlot(slot);
            d.ChangeHostIndex = 0x09;
            d.ReprogControlsIndex = 0x07;
            d.LinkUp = true;
            d.Wpid = wpid;
            if (bindings.Length > 0)
            {
                var dict = new Dictionary<byte, HostBinding>();
                foreach (var (h, hostName) in bindings)
                    dict[h] = new HostBinding(h, Paired: hostName is not null, HostIdentifier: null, ReceiverName: hostName);
                d.HostBindings = dict;
            }
            return d;
        }

        public void Dispose()
        {
            _sub.Dispose();
            _filteredSub.Dispose();
            Correlator.Dispose();
            Announcements.Dispose();
            Switcher.Dispose();
            Manager.Dispose();
        }
    }

    private static DeviceEntry MakeDevice(string serial, params (byte slot, string host)[] slotMap)
    {
        var d = new DeviceEntry { Serial = serial, WpidHex = "AAAA", Slot = 1, LinkUp = true };
        foreach (var (slot, host) in slotMap)
            d.SlotMap.Add(new DeviceSlotEntry { HostIndex = slot, Paired = true, HostName = host });
        return d;
    }

    [Fact]
    public void Prune_drops_devices_whose_slot_map_does_not_reference_our_host()
    {
        var source = new ReceiverAnnouncement
        {
            Hostname = PeerHost,
            Receivers =
            {
                new ReceiverAnnouncementEntry
                {
                    Serial = "REM-1",
                    Devices =
                    {
                        MakeDevice("D-A", (0, "other-1"), (1, "other-2")),
                        MakeDevice("D-B", (0, LocalHost), (1, PeerHost)),
                    },
                },
            },
        };

        var pruned = TopologyCorrelator.Prune(source, LocalHost);

        Assert.NotNull(pruned);
        var receiver = Assert.Single(pruned!.Receivers);
        var dev = Assert.Single(receiver.Devices);
        Assert.Equal("D-B", dev.Serial);
    }

    [Fact]
    public void Prune_drops_receivers_with_no_surviving_devices()
    {
        var source = new ReceiverAnnouncement
        {
            Hostname = PeerHost,
            Receivers =
            {
                new ReceiverAnnouncementEntry
                {
                    Serial = "REM-empty",
                    Devices = { MakeDevice("D-A", (0, "other")), },
                },
                new ReceiverAnnouncementEntry
                {
                    Serial = "REM-good",
                    Devices = { MakeDevice("D-B", (0, LocalHost)), },
                },
            },
        };

        var pruned = TopologyCorrelator.Prune(source, LocalHost);

        Assert.NotNull(pruned);
        var receiver = Assert.Single(pruned!.Receivers);
        Assert.Equal("REM-good", receiver.Serial);
    }

    [Fact]
    public void Prune_returns_null_when_no_device_references_us()
    {
        var source = new ReceiverAnnouncement
        {
            Hostname = PeerHost,
            Receivers =
            {
                new ReceiverAnnouncementEntry
                {
                    Devices = { MakeDevice("D-A", (0, "other")), },
                },
            },
        };

        Assert.Null(TopologyCorrelator.Prune(source, LocalHost));
    }

    [Fact]
    public void Announcement_with_switch_event_to_peer_triggers_cross_machine_fan_out()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        // Local keyboard with a slot for the peer host — eligible to follow.
        f.SeedDevice(r, 1, wpid: 0xBBBB, (0, LocalHost), (1, PeerHost));

        f.Announcements.OnNext(new ReceiverAnnouncement
        {
            Hostname = PeerHost,
            Receivers =
            {
                new ReceiverAnnouncementEntry
                {
                    Serial = "REM-1",
                    Devices =
                    {
                        // A shared device (different physical device from
                        // keyboard) that references us — passes the filter.
                        MakeDevice("D-shared", (0, LocalHost), (1, PeerHost)),
                    },
                },
            },
            LastSwitchEvent = new SwitchEvent
            {
                DeviceSerial = "D-shared",
                TargetHostName = PeerHost,
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            },
        });

        Assert.Single(f.Filtered);
        var ev = Assert.Single(f.FanOuts);
        Assert.Equal((byte)1, ev.Target.DeviceIndex);
        Assert.Equal((byte)1, ev.TargetHost);
        Assert.Equal(FanOutSource.RemoteTopology, ev.Source);
    }

    [Fact]
    public void Announcement_with_switch_event_targeting_us_does_not_fan_out()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        f.SeedDevice(r, 1, wpid: 0xBBBB, (0, LocalHost), (1, PeerHost));

        f.Announcements.OnNext(new ReceiverAnnouncement
        {
            Hostname = PeerHost,
            Receivers =
            {
                new ReceiverAnnouncementEntry
                {
                    Devices = { MakeDevice("D-shared", (0, LocalHost), (1, PeerHost)), },
                },
            },
            LastSwitchEvent = new SwitchEvent
            {
                DeviceSerial = "D-shared",
                TargetHostName = LocalHost, // arriving on US
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            },
        });

        Assert.Single(f.Filtered);
        Assert.Empty(f.FanOuts);
    }

    [Fact]
    public void Announcement_without_switch_event_does_not_fan_out_but_passes_filter()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();
        f.SeedDevice(r, 1, wpid: 0xBBBB, (0, LocalHost), (1, PeerHost));

        f.Announcements.OnNext(new ReceiverAnnouncement
        {
            Hostname = PeerHost,
            Receivers =
            {
                new ReceiverAnnouncementEntry
                {
                    Devices = { MakeDevice("D-shared", (0, LocalHost), (1, PeerHost)), },
                },
            },
            LastSwitchEvent = null,
        });

        Assert.Single(f.Filtered);
        Assert.Empty(f.FanOuts);
    }

    [Fact]
    public void Announcement_pruned_to_empty_does_not_emit_filtered()
    {
        using var f = new Fixture();
        f.Announcements.OnNext(new ReceiverAnnouncement
        {
            Hostname = PeerHost,
            Receivers =
            {
                new ReceiverAnnouncementEntry
                {
                    Devices = { MakeDevice("D-X", (0, "other")), },
                },
            },
            LastSwitchEvent = new SwitchEvent { TargetHostName = "other" },
        });

        Assert.Empty(f.Filtered);
        Assert.Empty(f.FanOuts);
    }
}
