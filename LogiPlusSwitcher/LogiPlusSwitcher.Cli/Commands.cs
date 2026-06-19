using DynamicData;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Hid;
using LogiPlusSwitcher.Core.HidPp;
using LogiPlusSwitcher.Core.Switcher;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogiPlusSwitcher.Cli;

internal static class Commands
{
    /// <summary>Logger factory injected by the Program bootstrap.</summary>
    public static ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;


    public static void PrintUsage()
    {
        Console.WriteLine("logiplus — companion switcher for Logitech Bolt receivers");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  logiplus list                       List receivers + paired devices (default if no command).");
        Console.WriteLine("  logiplus monitor                    Listen for Easy-Switch / Flow events and fan out.");
        Console.WriteLine("  logiplus switch <host>              Switch ALL paired devices to host (0..2).");
        Console.WriteLine("  logiplus device <slot> switch <host>  Switch a single slot (1..6) to host (0..2).");
        Console.WriteLine("  logiplus device <slot> unpair         Unpair slot (1..6) from the first receiver. Destructive.");
        Console.WriteLine("  logiplus device --receiver <idx> <slot> unpair");
        Console.WriteLine("  logiplus diag                       Dump every raw HID++ frame on the wire.");
        Console.WriteLine("  logiplus service install            Register as a background service / login agent.");
        Console.WriteLine("  logiplus service uninstall          Remove the background registration.");
        Console.WriteLine("  logiplus service status             Show service registration status.");
        Console.WriteLine("  logiplus help                       This message.");
    }

    public static async Task<int> RunListAsync(IReceiverTransport transport, CancellationToken ct)
    {
        var infos = transport.Enumerate();
        if (infos.Count == 0)
        {
            Console.WriteLine("No Bolt receivers found. Plug one in and confirm Input Monitoring permission on macOS.");
            return 1;
        }

        var index = 0;
        foreach (var info in infos)
        {
            Console.WriteLine($"Receiver #{index++}: {info.ManufacturerString} {info.ProductString}");
            Console.WriteLine($"  release: 0x{info.ReleaseNumber:X4}");
            Console.WriteLine($"  path:    {info.Path}");

            using var connection = transport.Open(info);
            using var receiver = new BoltReceiver(info, connection, logger: LoggerFactory.CreateLogger<BoltReceiver>());

            // Settle a brief window for paired-device 0x41 notifications.
            using var enumWait = new ManualResetEventSlim(false);
            using var linkUpSub = receiver.LinkEstablished.Subscribe(_ => enumWait.Set());
            using var linkLostSub = receiver.LinkLost.Subscribe(_ => enumWait.Set());
            receiver.Start();

            using var settleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            settleCts.CancelAfter(TimeSpan.FromMilliseconds(750));
            try
            {
                while (!settleCts.IsCancellationRequested)
                {
                    enumWait.Wait(settleCts.Token);
                    enumWait.Reset();
                }
            }
            catch (OperationCanceledException) { }

            // Pull extended receiver metadata (fw version, BLE address, serial).
            try
            {
                var details = await receiver.GetReceiverDetailsAsync(ct: ct);
                Console.WriteLine($"  serial:  {details.Serial ?? "(unreadable)"}");
                Console.WriteLine($"  fw:      {details.FirmwareVersionString}");
                if (details.BluetoothAddressString is { } ble)
                    Console.WriteLine($"  ble:     {ble}");
                Console.WriteLine($"  slots:   {details.MaxDevices}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  receiver details unavailable: {ex.Message}");
            }

            // Read per-slot metadata via Bolt registers (works even for link-down slots).
            // Iterate the maximum slot range so we pick up devices the 0x41 enumeration missed.
            for (byte slotIdx = HidPpConstants.DeviceIndexFirstSlot; slotIdx <= HidPpConstants.DeviceIndexLastSlot; slotIdx++)
            {
                await receiver.ReadSlotMetadataAsync(slotIdx, ct);
            }

            foreach (var device in receiver.Devices.Items.OrderBy(d => (int)d.DeviceIndex))
            {
                try
                {
                    await receiver.DiscoverFeaturesAsync(device.DeviceIndex, ct);
                    if (device.HostsInfoIndex is { } hostsIndex)
                    {
                        var hosts = await receiver.HostsInfo.GetHostsInfoAsync(device.DeviceIndex, hostsIndex, ct);
                        device.LastKnownCurrentHost = hosts.CurrentHost;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"    slot {device.DeviceIndex} discover failed: {ex.Message}");
                }
                Console.WriteLine($"  {device}");
            }

            if (receiver.Devices.Count == 0)
                Console.WriteLine("  (no paired devices reported within 750 ms)");
            Console.WriteLine();
        }

        return 0;
    }

    public static async Task<int> RunMonitorAsync(IReceiverTransport transport, CancellationToken ct, bool dumpFrames = false)
    {
        var perReceiver = new Dictionary<string, ReceiverSubscriptions>();

        using var manager = new ReceiverManager(transport, pollInterval: TimeSpan.FromSeconds(2), loggerFactory: LoggerFactory);
        using var attachFailSub = manager.AttachFailures.Subscribe(ex =>
            Console.Error.WriteLine($"  !! attach failed: {ex.Message}"));

        using var receiverChangesSub = manager.Receivers.Connect().Subscribe(changes =>
        {
            foreach (var change in changes)
            {
                switch (change.Reason)
                {
                    case ChangeReason.Add:
                        var receiver = change.Current;
                        Console.WriteLine($"  ** receiver attached: {receiver.Info.ProductString} (serial {receiver.Info.Serial})");
                        perReceiver[receiver.Info.Path] = WireReceiver(receiver, ct, dumpFrames);
                        break;

                    case ChangeReason.Remove:
                        var path = change.Key;
                        Console.WriteLine($"  ** receiver detached: serial {change.Current.Info.Serial}");
                        if (perReceiver.Remove(path, out var subs))
                            subs.Dispose();
                        break;
                }
            }
        });

        Console.WriteLine("Monitoring. Hot-plug supported. Ctrl+C to stop.");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }

        Console.WriteLine();
        Console.WriteLine("Restoring CID divert state on all receivers...");
        foreach (var receiver in manager.Receivers.Items)
        {
            foreach (var device in receiver.Devices.Items)
            {
                try
                {
                    await receiver.RestoreHostSwitchCidsAsync(device.DeviceIndex, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  restore {receiver.Info.Serial} slot {device.DeviceIndex} failed: {ex.Message}");
                }
            }
        }

        foreach (var subs in perReceiver.Values)
            subs.Dispose();

        return 0;
    }

    private static ReceiverSubscriptions WireReceiver(BoltReceiver receiver, CancellationToken ct, bool dumpFrames)
    {
        var subs = new ReceiverSubscriptions();

        if (dumpFrames)
            subs.Add(receiver.RawFrames.Subscribe(frame =>
                Console.WriteLine($"  IN  [{receiver.Info.Serial}] {frame}")));

        subs.Add(receiver.LinkEstablished.Subscribe(device =>
        {
            Console.WriteLine($"  ++  [{receiver.Info.Serial}] slot {device.DeviceIndex} link UP wpid=0x{device.Wpid:X4}");
            Task.Run(() => DiscoverAndDivertAsync(receiver, device.DeviceIndex, ct));
        }));
        subs.Add(receiver.LinkLost.Subscribe(device =>
            Console.WriteLine($"  --  [{receiver.Info.Serial}] slot {device.DeviceIndex} link LOST")));
        subs.Add(receiver.HostSwitchPresses.Subscribe(ev =>
            Console.WriteLine($"  >>  [{receiver.Info.Serial}] Easy-Switch slot {ev.DeviceIndex} -> host {ev.TargetHost} (cid 0x{ev.PrimaryCid:X4})")));
        subs.Add(receiver.FlowHostSwitches.Subscribe(snoop =>
            Console.WriteLine($"  >>  [{receiver.Info.Serial}] Flow snoop: slot {snoop.DeviceIndex} -> host {snoop.TargetHost} (sw_id 0x{snoop.SwId:X1})")));

        var switcher = new SwitcherService(receiver, LoggerFactory.CreateLogger<SwitcherService>());
        subs.Add(switcher);
        subs.Add(switcher.FanOuts.Subscribe(ev =>
            Console.WriteLine($"  ->  [{receiver.Info.Serial}] fan-out host={ev.TargetHost} slot={ev.Target.DeviceIndex} src={ev.Source}")));

        return subs;
    }

    public static async Task<int> RunSwitchAllAsync(IReceiverTransport transport, byte targetHost, CancellationToken ct)
    {
        var infos = transport.Enumerate();
        if (infos.Count == 0)
        {
            Console.WriteLine("No Bolt receivers found.");
            return 1;
        }

        var info = infos[0];
        using var connection = transport.Open(info);
        using var receiver = new BoltReceiver(info, connection);

        using var settled = new ManualResetEventSlim(false);
        using var sub = receiver.LinkEstablished.Subscribe(_ => settled.Set());
        receiver.Start();
        settled.Wait(TimeSpan.FromMilliseconds(750), ct);

        var sent = 0;
        foreach (var device in receiver.Devices.Items)
        {
            try
            {
                await receiver.DiscoverFeaturesAsync(device.DeviceIndex, ct);
            }
            catch
            {
                continue;
            }

            if (receiver.TrySwitchHost(device.DeviceIndex, targetHost))
            {
                Console.WriteLine($"  -> slot {device.DeviceIndex} switching to host {targetHost}");
                sent++;
            }
        }

        if (sent == 0)
        {
            Console.WriteLine("No paired devices accepted a CHANGE_HOST write.");
            return 2;
        }
        return 0;
    }

    public static async Task<int> RunRenameSlotAsync(IReceiverTransport transport, byte slot, string newName, CancellationToken ct, int receiverIndex = 0)
    {
        var infos = transport.Enumerate();
        if (infos.Count == 0 || receiverIndex >= infos.Count)
        {
            Console.WriteLine("No matching Bolt receiver.");
            return 1;
        }

        var info = infos[receiverIndex];
        using var connection = transport.Open(info);
        using var receiver = new BoltReceiver(info, connection, logger: LoggerFactory.CreateLogger<BoltReceiver>());

        using var settled = new ManualResetEventSlim(false);
        using var sub = receiver.LinkEstablished.Subscribe(_ => settled.Set());
        using var lostSub = receiver.LinkLost.Subscribe(_ => settled.Set());
        receiver.Start();
        settled.Wait(TimeSpan.FromMilliseconds(750), ct);

        // Read current name first so we have something to display.
        var oldName = await receiver.ReadSlotNameAsync(slot, ct) ?? "(unknown)";
        Console.WriteLine($"Renaming slot {slot}: \"{oldName}\" -> \"{newName}\"");
        try
        {
            var ok = await receiver.RenameDeviceAsync(slot, newName, ct: ct);
            if (!ok) { Console.WriteLine("  timed out — name may or may not have been written."); return 2; }

            var readBack = await receiver.ReadSlotNameAsync(slot, ct);
            Console.WriteLine($"  ok — read back: \"{readBack}\"");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  rename failed: {ex.Message}");
            return 3;
        }
    }

    public static async Task<int> RunUnpairSlotAsync(IReceiverTransport transport, byte slot, CancellationToken ct, int receiverIndex = 0)
    {
        var infos = transport.Enumerate();
        if (infos.Count == 0)
        {
            Console.WriteLine("No Bolt receivers found.");
            return 1;
        }
        if (receiverIndex >= infos.Count)
        {
            Console.WriteLine($"Receiver index {receiverIndex} out of range (have {infos.Count}).");
            return 1;
        }

        var info = infos[receiverIndex];
        using var connection = transport.Open(info);
        using var receiver = new BoltReceiver(info, connection, logger: LoggerFactory.CreateLogger<BoltReceiver>());

        using var settled = new ManualResetEventSlim(false);
        using var sub = receiver.LinkEstablished.Subscribe(_ => settled.Set());
        using var lostSub = receiver.LinkLost.Subscribe(_ => settled.Set());
        receiver.Start();
        settled.Wait(TimeSpan.FromMilliseconds(750), ct);

        try
        {
            Console.WriteLine($"Unpairing slot {slot} on receiver {info.ProductString} (serial {info.Serial})...");
            var ok = await receiver.UnpairAsync(slot, ct: ct);
            if (ok)
            {
                Console.WriteLine($"  ok — slot {slot} removed.");
                return 0;
            }
            Console.WriteLine($"  timed out waiting for confirmation; slot may or may not be removed.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  unpair failed: {ex.Message}");
            return 3;
        }
    }

    public static async Task<int> RunSwitchSlotAsync(IReceiverTransport transport, byte slot, byte targetHost, CancellationToken ct)
    {
        var infos = transport.Enumerate();
        if (infos.Count == 0)
        {
            Console.WriteLine("No Bolt receivers found.");
            return 1;
        }

        var info = infos[0];
        using var connection = transport.Open(info);
        using var receiver = new BoltReceiver(info, connection);

        using var settled = new ManualResetEventSlim(false);
        using var sub = receiver.LinkEstablished.Subscribe(device =>
        {
            if (device.DeviceIndex == slot) settled.Set();
        });
        receiver.Start();
        settled.Wait(TimeSpan.FromMilliseconds(750), ct);

        try
        {
            await receiver.DiscoverFeaturesAsync(slot, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"discover slot {slot} failed: {ex.Message}");
            return 3;
        }

        if (!receiver.TrySwitchHost(slot, targetHost))
        {
            Console.WriteLine($"slot {slot} does not support CHANGE_HOST (or is offline).");
            return 2;
        }
        Console.WriteLine($"-> slot {slot} switching to host {targetHost}");
        return 0;
    }

    private static async Task DiscoverAndDivertAsync(BoltReceiver receiver, byte deviceIndex, CancellationToken ct)
    {
        try
        {
            await receiver.DiscoverFeaturesAsync(deviceIndex, ct);
            var device = receiver.TryGetDevice(deviceIndex);
            if (device is null) return;

            Console.WriteLine($"      feats slot {deviceIndex}: 1B04={device.ReprogControlsIndex?.ToString("X2") ?? "-"} " +
                              $"1814={device.ChangeHostIndex?.ToString("X2") ?? "-"} " +
                              $"1815={device.HostsInfoIndex?.ToString("X2") ?? "-"}");

            if (device.ReprogControlsIndex is not null)
            {
                var diverted = await receiver.DivertHostSwitchCidsAsync(deviceIndex, persistent: false, ct);
                if (diverted.Count > 0)
                    Console.WriteLine($"      diverted slot {deviceIndex}: {string.Join(", ", diverted.Select(c => $"0x{c:X4}"))}");
            }

            if (device.HostsInfoIndex is { } hostsIndex)
            {
                var hosts = await receiver.HostsInfo.GetHostsInfoAsync(deviceIndex, hostsIndex, ct);
                device.LastKnownCurrentHost = hosts.CurrentHost;
                Console.WriteLine($"      hosts slot {deviceIndex}: current={hosts.CurrentHost}/{hosts.NumberOfHosts}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"      discover slot {deviceIndex} failed: {ex.Message}");
        }
    }

    /// <summary>Bag of per-receiver disposables for the monitor command.</summary>
    private sealed class ReceiverSubscriptions : IDisposable
    {
        private readonly List<IDisposable> _items = new();
        public void Add(IDisposable d) => _items.Add(d);
        public void Dispose()
        {
            foreach (var d in _items) try { d.Dispose(); } catch { /* swallow */ }
            _items.Clear();
        }
    }
}
