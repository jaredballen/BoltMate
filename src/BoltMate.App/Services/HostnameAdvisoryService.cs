using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using BoltMate.Core.Bolt;
using BoltMate.Core.Services;
using BoltMate.Core.Topology;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BoltMate.App.Services;

/// <summary>
/// Surfaces "another machine signed into your account isn't paired with
/// your peripherals" advisories.
/// </summary>
/// <remarks>
/// <para>Trust ring is now cryptographic — a peer ReceiverAnnouncement
/// only reaches us if the sender holds our account's SyncKey. So the
/// existing hostname-in-our-HostBindings filter is no longer a security
/// check; it's a UX signal.</para>
///
/// <para>When a peer's hostname doesn't appear in any local device's
/// <c>HostBindings.ReceiverName</c>, that means the user has signed into
/// BoltMate on that machine but hasn't paired their Logitech peripherals
/// to it yet (or the hostname changed since they did). The fan-out
/// router can't route a switch to that peer because no local device
/// "knows" about its host slot. Surfaced so the user can either pair
/// in Logi Options+ or rename the host.</para>
///
/// <para>Backed by the <see cref="IUdpTopologyService.Announcements"/>
/// stream — every successful decrypt-and-deserialize lands here. We
/// dedup by peer machineId so a flapping peer doesn't flood the
/// advisories stream.</para>
/// </remarks>
public sealed class HostnameAdvisoryService : IDisposable
{
    private readonly IReceiverManager _receivers;
    private readonly ILogger _log;
    private readonly CompositeDisposable _disposables = new();
    private readonly Subject<PeerHostnameAdvisory> _advisories = new();
    private readonly ConcurrentDictionary<string, string> _announced = new(); // machineId → hostname
    private readonly ConcurrentDictionary<string, byte> _advisedMachineIds = new();

    public HostnameAdvisoryService(
        IUdpTopologyService topology,
        IReceiverManager receivers,
        ILogger<HostnameAdvisoryService>? logger = null)
    {
        _receivers = receivers;
        _log = logger ?? NullLogger<HostnameAdvisoryService>.Instance;

        _disposables.Add(topology.Announcements.Subscribe(OnAnnouncement));
        _disposables.Add(_advisories);

        // Re-evaluate when local devices change — a new pairing may
        // turn an existing advisory into a no-op.
        _disposables.Add(receivers.Receivers.Connect()
            .Subscribe(_ => ReevaluateKnownPeers()));
    }

    /// <summary>Stream of (hostname, machineId) pairs whose hostnames aren't represented in any local device's HostBindings.</summary>
    public IObservable<PeerHostnameAdvisory> Advisories => _advisories.AsObservable();

    /// <summary>Current snapshot of advisories — useful for "Status" tab queries.</summary>
    public IReadOnlyList<PeerHostnameAdvisory> CurrentAdvisories()
    {
        var known = KnownHostnames();
        return _announced
            .Where(kv => !known.Contains(kv.Value))
            .Select(kv => new PeerHostnameAdvisory(kv.Key, kv.Value))
            .ToArray();
    }

    private void OnAnnouncement(ReceiverAnnouncement ann)
    {
        if (string.IsNullOrEmpty(ann.MachineId) || string.IsNullOrEmpty(ann.Hostname)) return;
        _announced[ann.MachineId] = ann.Hostname;

        if (KnownHostnames().Contains(ann.Hostname))
        {
            _advisedMachineIds.TryRemove(ann.MachineId, out _);
            return;
        }

        // Dedup: a flapping peer with the same machineId only raises
        // one advisory until we see its hostname land in our bindings.
        if (!_advisedMachineIds.TryAdd(ann.MachineId, 0)) return;

        var advisory = new PeerHostnameAdvisory(ann.MachineId, ann.Hostname);
        _log.LogInformation("Peer {MachineId} announces hostname '{Hostname}' but no local device's HostBindings reference it — peripheral pairing on that machine is needed for fan-out.",
            ann.MachineId, ann.Hostname);
        _advisories.OnNext(advisory);
    }

    private void ReevaluateKnownPeers()
    {
        var known = KnownHostnames();
        foreach (var (machineId, hostname) in _announced)
        {
            if (known.Contains(hostname)) _advisedMachineIds.TryRemove(machineId, out _);
        }
    }

    private HashSet<string> KnownHostnames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var receiver in _receivers.Receivers.Items)
        {
            foreach (var device in receiver.Devices.Items)
            {
                foreach (var (_, binding) in device.HostBindings)
                {
                    if (!string.IsNullOrEmpty(binding.ReceiverName))
                        names.Add(binding.ReceiverName);
                }
            }
        }
        return names;
    }

    public void Dispose() => _disposables.Dispose();
}

public sealed record PeerHostnameAdvisory(string MachineId, string Hostname);
