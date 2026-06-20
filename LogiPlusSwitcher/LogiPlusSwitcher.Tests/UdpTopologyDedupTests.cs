using DynamicData;
using LogiPlusSwitcher.Core;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Topology;
using LogiPlusSwitcher.Tests.Support;
using Xunit;

namespace LogiPlusSwitcher.Tests;

/// <summary>
/// Pure-data coverage for <see cref="UdpTopologyService.InjectInbound"/> —
/// the dedup-by-seq + per-peer stats + mutual-ack logic. Bypasses the UDP
/// socket entirely (network-free) by feeding announcements through the
/// public Inject API the way the mDNS+TCP channel does.
/// </summary>
public class UdpTopologyDedupTests
{
    private sealed class Fixture : IDisposable
    {
        public FakeReceiverTransport Transport { get; } = new();
        public ReceiverManager Manager { get; }
        public UdpTopologyService Service { get; }
        public List<ReceiverAnnouncement> Received { get; } = new();
        private readonly IDisposable _sub;

        public Fixture(string machineId = "us-machine-id")
        {
            // Bind to a port the test process is unlikely to be using; we never
            // actually call Start() so no socket is created.
            Manager = new ReceiverManager(Transport, autoStart: false);
            Service = new UdpTopologyService(Manager,
                new TopologySettings { Port = 0, UseMulticast = false, RepeatCount = 1 },
                machineId);
            _sub = Service.Announcements.Subscribe(a => Received.Add(a));
        }

        public void Dispose()
        {
            _sub.Dispose();
            Service.Dispose();
            Manager.Dispose();
        }
    }

    private static ReceiverAnnouncement Make(string machineId, ulong seq, params (string Peer, ulong Seq)[] acks)
    {
        var ann = new ReceiverAnnouncement
        {
            MachineId = machineId,
            Hostname = $"host-{machineId}",
            Seq = seq,
            Timestamp = "2026-06-20T00:00:00Z",
        };
        foreach (var (p, s) in acks)
            ann.Acks.Add(new PeerAck { MachineId = p, LastSeq = s });
        return ann;
    }

    [Fact]
    public void Drops_own_machine_id_echo()
    {
        using var f = new Fixture("us");
        f.Service.InjectInbound(Make("us", 42));
        Assert.Empty(f.Received);
    }

    [Fact]
    public void Forwards_first_announcement_from_new_peer()
    {
        using var f = new Fixture();
        f.Service.InjectInbound(Make("peer-A", 1));
        Assert.Single(f.Received);

        var stats = f.Service.PeerSnapshot.Single();
        Assert.Equal("peer-A", stats.MachineId);
        Assert.Equal((ulong)1, stats.LastSeq);
        Assert.Equal(1, stats.UniqueReceived);
        Assert.Equal(0, stats.MissedFromPeer);
    }

    [Fact]
    public void Suppresses_duplicate_with_same_or_lower_seq()
    {
        using var f = new Fixture();
        f.Service.InjectInbound(Make("peer-A", 5));
        f.Service.InjectInbound(Make("peer-A", 5)); // exact dup (N× repeat)
        f.Service.InjectInbound(Make("peer-A", 3)); // out-of-order older

        Assert.Single(f.Received);
        var stats = f.Service.PeerSnapshot.Single();
        Assert.Equal(1, stats.UniqueReceived);
        Assert.True(stats.DuplicatesSuppressed >= 2);
    }

    [Fact]
    public void Detects_seq_gap_as_inbound_packet_loss()
    {
        using var f = new Fixture();
        f.Service.InjectInbound(Make("peer-A", 1));
        f.Service.InjectInbound(Make("peer-A", 5)); // jump: missed 2,3,4

        Assert.Equal(2, f.Received.Count);
        var stats = f.Service.PeerSnapshot.Single();
        Assert.Equal(3, stats.MissedFromPeer);
    }

    [Fact]
    public void Mutual_ack_records_peer_ack_of_our_seq()
    {
        using var f = new Fixture("us");
        // Peer's announcement echoes that they last saw our seq = 100.
        f.Service.InjectInbound(Make("peer-A", 1, ("us", 100)));

        var stats = f.Service.PeerSnapshot.Single();
        Assert.Equal((ulong)100, stats.LastAckOfOurSeq);
    }

    [Fact]
    public void Mutual_ack_ignores_ack_for_other_machine()
    {
        using var f = new Fixture("us");
        f.Service.InjectInbound(Make("peer-A", 1,
            ("not-us-1", 50),
            ("not-us-2", 75)));

        var stats = f.Service.PeerSnapshot.Single();
        Assert.Equal((ulong)0, stats.LastAckOfOurSeq);
    }

    [Fact]
    public void LatestPeerAnnouncements_keeps_most_recent_per_peer()
    {
        using var f = new Fixture();
        f.Service.InjectInbound(Make("peer-A", 1));
        f.Service.InjectInbound(Make("peer-A", 2));
        f.Service.InjectInbound(Make("peer-B", 1));

        var latest = f.Service.LatestPeerAnnouncements;
        Assert.Equal(2, latest.Count);
        Assert.Contains(latest, a => a.MachineId == "peer-A" && a.Seq == 2);
        Assert.Contains(latest, a => a.MachineId == "peer-B" && a.Seq == 1);
    }
}
