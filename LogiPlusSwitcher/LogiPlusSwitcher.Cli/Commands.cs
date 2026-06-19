using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Hid;
using LogiPlusSwitcher.Core.HidPp.Features;
using LogiPlusSwitcher.Core.Switcher;

namespace LogiPlusSwitcher.Cli;

internal static class Commands
{
    public static void PrintUsage()
    {
        Console.WriteLine("logiplus — companion switcher for Logitech Bolt receivers");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  logiplus list                       List receivers + paired devices (default if no command).");
        Console.WriteLine("  logiplus monitor                    Listen for Easy-Switch / Flow events and fan out.");
        Console.WriteLine("  logiplus switch <host>              Switch ALL paired devices to host (0..2).");
        Console.WriteLine("  logiplus device <slot> switch <host>  Switch a single slot (1..6) to host (0..2).");
        Console.WriteLine("  logiplus diag                       Dump every raw HID++ frame on the wire.");
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

        for (var i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            Console.WriteLine($"Receiver #{i}: {info.ManufacturerString} {info.ProductString}");
            Console.WriteLine($"  serial:  {info.Serial}");
            Console.WriteLine($"  release: 0x{info.ReleaseNumber:X4}");
            Console.WriteLine($"  path:    {info.Path}");

            using var connection = transport.Open(info);
            using var receiver = new BoltReceiver(info, connection);

            var slots = new List<PairedDevice>();
            using var enumWait = new ManualResetEventSlim(false);
            using var doneCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            doneCts.CancelAfter(TimeSpan.FromMilliseconds(750));
            receiver.DeviceLinkEstablished += (_, _) => enumWait.Set();
            receiver.DeviceLinkLost += (_, _) => enumWait.Set();
            receiver.Start();

            try
            {
                while (!doneCts.IsCancellationRequested)
                {
                    enumWait.Wait(doneCts.Token);
                    enumWait.Reset();
                }
            }
            catch (OperationCanceledException)
            {
                // Soft timeout: paired devices that responded are now in receiver.Devices.
            }

            foreach (var device in receiver.Devices.OrderBy(d => d.DeviceIndex))
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
            slots.AddRange(receiver.Devices);

            if (slots.Count == 0)
                Console.WriteLine("  (no paired devices reported within 750 ms)");
            Console.WriteLine();
        }

        return 0;
    }

    public static async Task<int> RunMonitorAsync(IReceiverTransport transport, CancellationToken ct, bool dumpFrames = false)
    {
        var infos = transport.Enumerate();
        if (infos.Count == 0)
        {
            Console.WriteLine("No Bolt receivers found.");
            return 1;
        }

        var info = infos[0];
        Console.WriteLine($"Monitoring {info.ProductString} (serial {info.Serial}).");

        using var connection = transport.Open(info);
        using var receiver = new BoltReceiver(info, connection);
        using var switcher = new SwitcherService(receiver);

        if (dumpFrames)
            receiver.RawFrameReceived += (_, frame) => Console.WriteLine($"  IN  {frame}");

        receiver.DeviceLinkEstablished += (_, device) =>
        {
            Console.WriteLine($"  ++  slot {device.DeviceIndex} link UP wpid=0x{device.Wpid:X4}");
            Task.Run(() => DiscoverAndDivertAsync(receiver, device.DeviceIndex, ct));
        };
        receiver.DeviceLinkLost += (_, device) =>
            Console.WriteLine($"  --  slot {device.DeviceIndex} link LOST");
        receiver.HostSwitchPressed += (_, ev) =>
            Console.WriteLine($"  >>  Easy-Switch slot {ev.DeviceIndex} -> host {ev.TargetHost} (cid 0x{ev.PrimaryCid:X4})");
        receiver.FlowHostSwitchDetected += (_, snoop) =>
            Console.WriteLine($"  >>  Flow snoop:  slot {snoop.DeviceIndex} -> host {snoop.TargetHost} (sw_id 0x{snoop.SwId:X1})");
        switcher.FanOutIssued += (_, ev) =>
            Console.WriteLine($"  ->  fan-out host={ev.TargetHost} slot={ev.Target.DeviceIndex} src={ev.Source}");

        receiver.Start();
        Console.WriteLine("Listening. Ctrl+C to stop.");

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected on Ctrl+C.
        }

        Console.WriteLine();
        Console.WriteLine("Restoring CID divert state...");
        foreach (var device in receiver.Devices)
        {
            try
            {
                await receiver.RestoreHostSwitchCidsAsync(device.DeviceIndex, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  restore slot {device.DeviceIndex} failed: {ex.Message}");
            }
        }
        return 0;
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
        receiver.DeviceLinkEstablished += (_, _) => settled.Set();
        receiver.Start();
        settled.Wait(TimeSpan.FromMilliseconds(750), ct);

        var sent = 0;
        foreach (var device in receiver.Devices)
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
        receiver.DeviceLinkEstablished += (_, ev) =>
        {
            if (ev.DeviceIndex == slot) settled.Set();
        };
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
}
