using System.Reactive.Linq;
using BoltMate.Core;
using BoltMate.Core.Services;
using BoltMate.Core.Topology;
using BoltMate.Tests.Support;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BoltMate.Tests;

/// <summary>
/// Covers the permission-gate behaviour we added to the topology services:
/// starting in PermissionDenied when the gate is closed, transitioning to
/// the normal startup path once the gate flips open.
/// </summary>
public class PermissionGatedTransportTests
{
    private static TopologySettings MakeSettings() => new()
    {
        Enabled = true,
        Port = 0,                  // OS-assigned ephemeral so we never race other test sockets
        UseMulticast = false,      // skip multicast — no loopback needed for these assertions
        TcpPort = 0,
        MdnsServiceType = "_boltmate-test._tcp.local",
        MachineId = "test-machine",
    };

    [Fact]
    public void UdpTopology_starts_in_PermissionDenied_when_gate_closed()
    {
        var transport = new Support.FakeReceiverTransport();
        using var manager = new ReceiverManager(transport, autoStart: false);
        var perm = new FakePermission("network", initial: false);
        var time = new FakeTimeProvider();

        using var topology = new UdpTopologyService(manager, MakeSettings(),
            machineId: "m1",
            networkPermission: perm,
            timeProvider: time);
        topology.Start();

        TransportHealth? observed = null;
        using var sub = topology.UdpHealth.Subscribe(h => observed = h);

        Assert.NotNull(observed);
        Assert.Equal(TransportState.PermissionDenied, observed!.State);
    }

    [Fact]
    public void UdpTopology_transitions_to_Unknown_or_Healthy_once_permission_granted()
    {
        var transport = new Support.FakeReceiverTransport();
        using var manager = new ReceiverManager(transport, autoStart: false);
        var perm = new FakePermission("network", initial: false);

        using var topology = new UdpTopologyService(manager, MakeSettings(),
            machineId: "m1",
            networkPermission: perm);
        topology.Start();

        var states = new List<TransportState>();
        using var sub = topology.UdpHealth.Subscribe(h => states.Add(h.State));

        perm.Set(true);

        // First emission is PermissionDenied; after grant we expect to leave that bucket.
        Assert.Equal(TransportState.PermissionDenied, states[0]);
        Assert.DoesNotContain(TransportState.PermissionDenied, states.Skip(1));
    }

    [Fact]
    public void MdnsTcp_starts_in_PermissionDenied_when_gate_closed()
    {
        var transport = new Support.FakeReceiverTransport();
        using var manager = new ReceiverManager(transport, autoStart: false);
        var udpPerm = new FakePermission("network", initial: true);
        using var topology = new UdpTopologyService(manager, MakeSettings(),
            machineId: "m1",
            networkPermission: udpPerm);

        var mdnsPerm = new FakePermission("network", initial: false);
        using var channel = new MdnsTcpChannel(topology, MakeSettings(),
            machineId: "m1",
            networkPermission: mdnsPerm);
        channel.Start();

        TransportHealth? mdns = null, tcp = null;
        using var s1 = channel.MdnsHealth.Subscribe(h => mdns = h);
        using var s2 = channel.TcpHealth.Subscribe(h => tcp = h);

        Assert.NotNull(mdns);
        Assert.NotNull(tcp);
        Assert.Equal(TransportState.PermissionDenied, mdns!.State);
        Assert.Equal(TransportState.PermissionDenied, tcp!.State);
    }

    [Fact]
    public void MdnsTcp_SyncHealth_surfaces_PermissionDenied_when_either_path_denied()
    {
        var transport = new Support.FakeReceiverTransport();
        using var manager = new ReceiverManager(transport, autoStart: false);
        var udpPerm = new FakePermission("network", initial: true);
        using var topology = new UdpTopologyService(manager, MakeSettings(),
            machineId: "m1",
            networkPermission: udpPerm);

        var mdnsPerm = new FakePermission("network", initial: false);
        using var channel = new MdnsTcpChannel(topology, MakeSettings(),
            machineId: "m1",
            networkPermission: mdnsPerm);

        TransportHealth? sync = null;
        using var sub = channel.SyncHealth.Subscribe(h => sync = h);

        Assert.NotNull(sync);
        Assert.Equal(TransportState.PermissionDenied, sync!.State);
    }
}
