using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Hid;

LogiPlusSwitcher.Core.Hid.HidApiBridge.EnsureNativeLibraryResolver();
LogiPlusSwitcher.Core.Hid.HidApiBridge.SetMacOsNonExclusive();

Console.WriteLine($"libhidapi version: {HidApi.Hid.VersionString()}");

Console.WriteLine();
Console.WriteLine("All Logitech HID interfaces (raw enumeration):");
var allLogitech = HidApi.Hid.Enumerate(BoltConstants.LogitechVendorId, 0).ToList();
foreach (var info in allLogitech)
{
    Console.WriteLine($"  PID 0x{info.ProductId:X4}  Usage 0x{info.UsagePage:X4}/0x{info.Usage:X4}  IF#{info.InterfaceNumber}  {info.ProductString}");
    Console.WriteLine($"    Path: {info.Path}");
}
Console.WriteLine($"  ({allLogitech.Count} interface(s) found)");
Console.WriteLine();

var transport = new HidApiReceiverTransport();
var receivers = transport.Enumerate();

if (receivers.Count == 0)
{
    Console.WriteLine("No Logitech Bolt receivers matched the management-interface filter.");
    if (allLogitech.Any(i => i.ProductId == BoltConstants.BoltReceiverProductId))
    {
        Console.WriteLine("Note: a Bolt receiver (PID 0xC548) IS attached but its HID++ management interface");
        Console.WriteLine("did not enumerate under UsagePage 0xFF00 / Usage 0x0001. Filter may need adjustment.");
    }
    else
    {
        Console.WriteLine("No device with PID 0xC548 attached. Plug in a Bolt receiver and rerun.");
        Console.WriteLine("On macOS, also confirm Input Monitoring permission for this terminal.");
    }
    return 1;
}

Console.WriteLine($"Found {receivers.Count} Bolt receiver(s):");
foreach (var r in receivers)
{
    Console.WriteLine($"  - {r.ManufacturerString} {r.ProductString}");
    Console.WriteLine($"    Serial: {r.Serial}  Release: 0x{r.ReleaseNumber:X4}");
    Console.WriteLine($"    Path:   {r.Path}");
}

Console.WriteLine();
Console.WriteLine("Opening first receiver and listening for raw HID++ traffic (Ctrl+C to stop)...");

var info0 = receivers[0];
using var connection = transport.Open(info0);

connection.FrameReceived += (_, frame) =>
{
    Console.WriteLine($"  IN  {frame}");
};
connection.ReadError += (_, ex) =>
{
    Console.Error.WriteLine($"  ERR read pump failed: {ex.Message}");
};

connection.Start();

var quit = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    quit.Set();
};

quit.Wait();
connection.Stop();
Console.WriteLine("Stopped.");
return 0;
