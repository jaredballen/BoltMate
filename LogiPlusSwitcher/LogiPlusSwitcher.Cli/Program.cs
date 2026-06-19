using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Hid;
using LogiPlusSwitcher.Core.HidPp.Features;
using LogiPlusSwitcher.Core.Switcher;

HidApiBridge.EnsureNativeLibraryResolver();
HidApiBridge.SetMacOsNonExclusive();

Console.WriteLine($"libhidapi version: {HidApi.Hid.VersionString()}");
Console.WriteLine();

var transport = new HidApiReceiverTransport();
var infos = transport.Enumerate();

if (infos.Count == 0)
{
    Console.WriteLine("No Logitech Bolt receivers found.");
    Console.WriteLine("Plug in a Bolt receiver (VID 0x046D PID 0xC548).");
    Console.WriteLine("On macOS confirm Input Monitoring permission for this terminal.");
    return 1;
}

Console.WriteLine($"Found {infos.Count} Bolt receiver(s):");
foreach (var info in infos)
    Console.WriteLine($"  {info.ManufacturerString} {info.ProductString} serial={info.Serial}");
Console.WriteLine();

var connection = transport.Open(infos[0]);
using var receiver = new BoltReceiver(infos[0], connection);
using var switcher = new SwitcherService(receiver);

receiver.RawFrameReceived += (_, frame) =>
    Console.WriteLine($"  IN  {frame}");

receiver.DeviceLinkEstablished += (_, device) =>
{
    Console.WriteLine($"  ++  slot {device.DeviceIndex} link UP wpid=0x{device.Wpid:X4}");
    _ = Task.Run(() => DiscoverAndDivert(receiver, device.DeviceIndex));
};

receiver.DeviceLinkLost += (_, device) =>
    Console.WriteLine($"  --  slot {device.DeviceIndex} link LOST");

receiver.HostSwitchPressed += (_, ev) =>
    Console.WriteLine($"  >>  Easy-Switch slot {ev.DeviceIndex} -> host {ev.TargetHost} (cid 0x{ev.PrimaryCid:X4})");

receiver.FlowHostSwitchDetected += (_, snoop) =>
    Console.WriteLine($"  >>  Flow snoop: slot {snoop.DeviceIndex} -> host {snoop.TargetHost} (sw_id 0x{snoop.SwId:X1})");

switcher.FanOutIssued += (_, ev) =>
    Console.WriteLine($"  ->  fan-out CHANGE_HOST host={ev.TargetHost} to {ev.Target}  (source={ev.Source})");

receiver.Start();

Console.WriteLine("Listening. Press Ctrl+C to stop.");
var quit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    quit.Set();
};
quit.Wait();

Console.WriteLine();
Console.WriteLine("Restoring CID divert state...");
foreach (var device in receiver.Devices)
{
    try
    {
        await receiver.RestoreHostSwitchCidsAsync(device.DeviceIndex);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  restore failed for slot {device.DeviceIndex}: {ex.Message}");
    }
}

Console.WriteLine("Stopped.");
return 0;

static async Task DiscoverAndDivert(BoltReceiver receiver, byte deviceIndex)
{
    try
    {
        await receiver.DiscoverFeaturesAsync(deviceIndex);
        var device = receiver.Devices.FirstOrDefault(d => d.DeviceIndex == deviceIndex);
        if (device is null)
            return;

        Console.WriteLine($"      feats slot {deviceIndex}: 1B04={device.ReprogControlsIndex?.ToString("X2") ?? "-"} 1814={device.ChangeHostIndex?.ToString("X2") ?? "-"} 1815={device.HostsInfoIndex?.ToString("X2") ?? "-"}");

        if (device.ReprogControlsIndex is not null)
        {
            var diverted = await receiver.DivertHostSwitchCidsAsync(deviceIndex);
            if (diverted.Count > 0)
                Console.WriteLine($"      diverted slot {deviceIndex}: {string.Join(", ", diverted.Select(c => $"0x{c:X4}"))}");
        }

        if (device.HostsInfoIndex is { } hostsIndex)
        {
            var info = await receiver.HostsInfo.GetHostsInfoAsync(deviceIndex, hostsIndex);
            device.LastKnownCurrentHost = info.CurrentHost;
            Console.WriteLine($"      hosts slot {deviceIndex}: current={info.CurrentHost}/{info.NumberOfHosts}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"      discover slot {deviceIndex} failed: {ex.Message}");
    }
}
