using BoltMate.Core.Services;
using System.Reactive.Subjects;
using DynamicData;
using BoltMate.Core.Bolt;
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
        public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; } =
            new(DateTimeOffset.Parse("2026-06-22T20:00:00Z"));
        private readonly IDisposable _sub;
        private readonly IDisposable _filteredSub;

        public Fixture(string localHost = LocalHost)
        {
            Manager = new ReceiverManager(Transport, autoStart: false);
            Switcher = new SwitcherService(Manager);
            _sub = Switcher.FanOuts.Subscribe(ev => FanOuts.Add(ev));
            Correlator = new TopologyCorrelator(Manager, Switcher, Announcements,
                new[] { localHost }, timeProvider: Time);
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

        var pruned = TopologyCorrelator.Prune(source, new[] { LocalHost });

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

        var pruned = TopologyCorrelator.Prune(source, new[] { LocalHost });

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

        Assert.Null(TopologyCorrelator.Prune(source, new[] { LocalHost }));
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

    [Theory]
    [InlineData("Jareds-M4-MBP", "Jareds-M4-MBP", true)]
    [InlineData("Jareds-M4-MBP", "jareds-m4-mbp", true)]
    [InlineData(" Jareds-M4-MBP ", "Jareds-M4-MBP", true)]
    [InlineData("Jared's M4 MacBook Pro", "Jared's M4 MacBook Pro", true)]
    // Domain stripping removed — DNS form vs friendly form must NOT collide;
    // the multi-alias LocalHostIdentity covers the local-machine case.
    [InlineData("Jareds-M4-MBP.allen.family", "Jareds-M4-MBP", false)]
    [InlineData("Jareds-M4-MBP", "Jareds-M4-MBP.local", false)]
    [InlineData("Jareds-M4-MBP", "Other-Host", false)]
    [InlineData("", "Other-Host", false)]
    [InlineData("Jareds-M4-MBP", null, false)]
    public void HostNameMatches_is_case_insensitive_exact_compare(string? name1, string? name2, bool expected)
    {
        var result = HostNameHelper.HostNameMatches(name1, name2);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Local_link_lost_followed_by_remote_reappearance_triggers_fan_out()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();

        // 1. Seed a keyboard that is currently LinkUp on us, and paired to PeerHost
        f.SeedDevice(r, 2, wpid: 0xBBBB, (0, LocalHost), (1, PeerHost));

        // 2. Seed the mouse on slot 1, WPID 0xAAAA, and paired to PeerHost
        f.SeedDevice(r, 1, wpid: 0xAAAA, (0, LocalHost), (1, PeerHost));

        // Ensure both devices are linkUp locally
        Assert.True(r.TryGetDevice(1)!.LinkUp);
        Assert.True(r.TryGetDevice(2)!.LinkUp);

        // 3. Trigger local link-lost for the mouse (0xAAAA)
        var session = f.Transport.Sessions.Single();
        var conn = session.LastConnection;
        // In DJ pairing notifications, low byte first, so 0xAAAA is AA, AA
        conn.Inject(HidPpFrame.TryParse([0x10, 0x01, 0x41, 0x10, 0x40, 0xAA, 0xAA])!.Value);

        // Verify that the mouse's LinkUp status is now false locally
        Assert.False(r.TryGetDevice(1)!.LinkUp);

        // 4. Simulate receiving a remote announcement from PeerHost showing that device 0xAAAA has appeared on PeerHost.
        var remoteAnnouncement = new ReceiverAnnouncement
        {
            Hostname = PeerHost,
            Receivers =
            {
                new ReceiverAnnouncementEntry
                {
                    Serial = "REM-1",
                    Devices =
                    {
                        new DeviceEntry
                        {
                            Slot = 1,
                            WpidHex = "AAAA",
                            Serial = "M-1",
                            Name = "Mouse",
                            LinkUp = true, // it has appeared on the remote host!
                            SlotMap =
                            {
                                new DeviceSlotEntry { HostIndex = 0, Paired = true, HostName = LocalHost },
                                new DeviceSlotEntry { HostIndex = 1, Paired = true, HostName = PeerHost }
                            }
                        }
                    }
                }
            },
            LastSwitchEvent = null // No explicit switch event (cycle button device)
        };

        // 5. Send announcement to correlator
        f.Announcements.OnNext(remoteAnnouncement);

        // 6. Verify that f.FanOuts contains a fan-out event for the keyboard (0xBBBB) fanning out to PeerHost (slot 1)
        var ev = Assert.Single(f.FanOuts);
        Assert.Equal((byte)2, ev.Target.DeviceIndex);
        Assert.Equal((byte)1, ev.TargetHost);
        Assert.Equal(FanOutSource.RemoteTopology, ev.Source);
    }

    [Fact]
    public void Reappearance_after_window_expires_does_not_trigger_fan_out()
    {
        // Time-mocked: link-lost stashes wpid at T0. Advance past the 10s
        // reappearance window. Remote announcement of the same wpid then
        // arrives → must NOT fan out (stale entry should have been purged).
        using var f = new Fixture();
        var r = f.AddReceiver();
        f.SeedDevice(r, 2, wpid: 0xBBBB, (0, LocalHost), (1, PeerHost));
        f.SeedDevice(r, 1, wpid: 0xAAAA, (0, LocalHost), (1, PeerHost));

        // Inject local link-lost for mouse 0xAAAA at T0.
        var conn = f.Transport.Sessions.Single().LastConnection;
        conn.Inject(HidPpFrame.TryParse([0x10, 0x01, 0x41, 0x10, 0x40, 0xAA, 0xAA])!.Value);

        // Advance past 10s reappearance window.
        f.Time.Advance(TimeSpan.FromSeconds(11));

        // Send the late remote announcement.
        var late = new ReceiverAnnouncement
        {
            Hostname = PeerHost,
            Receivers =
            {
                new ReceiverAnnouncementEntry
                {
                    Serial = "REM-1",
                    Devices =
                    {
                        new DeviceEntry
                        {
                            Slot = 1,
                            WpidHex = "AAAA",
                            Serial = "M-1",
                            LinkUp = true,
                            SlotMap =
                            {
                                new DeviceSlotEntry { HostIndex = 0, Paired = true, HostName = LocalHost },
                                new DeviceSlotEntry { HostIndex = 1, Paired = true, HostName = PeerHost },
                            },
                        },
                    },
                },
            },
        };
        f.Announcements.OnNext(late);

        Assert.Empty(f.FanOuts);
    }

    [Fact]
    public void Remote_reappearance_without_prior_local_link_lost_does_not_trigger_fan_out()
    {
        using var f = new Fixture();
        var r = f.AddReceiver();

        f.SeedDevice(r, 2, wpid: 0xBBBB, (0, LocalHost), (1, PeerHost));
        f.SeedDevice(r, 1, wpid: 0xAAAA, (0, LocalHost), (1, PeerHost));

        // Send a remote announcement showing 0xAAAA is LinkUp on PeerHost,
        // but since we never saw a local LinkLost for it, no fan-out should occur.
        var remoteAnnouncement = new ReceiverAnnouncement
        {
            Hostname = PeerHost,
            Receivers =
            {
                new ReceiverAnnouncementEntry
                {
                    Serial = "REM-1",
                    Devices =
                    {
                        new DeviceEntry
                        {
                            Slot = 1,
                            WpidHex = "AAAA",
                            Serial = "M-1",
                            Name = "Mouse",
                            LinkUp = true,
                            SlotMap =
                            {
                                new DeviceSlotEntry { HostIndex = 0, Paired = true, HostName = LocalHost },
                                new DeviceSlotEntry { HostIndex = 1, Paired = true, HostName = PeerHost }
                            }
                        }
                    }
                }
            },
            LastSwitchEvent = null
        };

        f.Announcements.OnNext(remoteAnnouncement);

        Assert.Empty(f.FanOuts);
    }
}
