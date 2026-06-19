using LogiPlusSwitcher.Cli;
using LogiPlusSwitcher.Core.Hid;

HidApiBridge.EnsureNativeLibraryResolver();
HidApiBridge.SetMacOsNonExclusive();

var transport = new HidApiReceiverTransport();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (args.Length == 0)
    return await Commands.RunListAsync(transport, cts.Token);

return args[0].ToLowerInvariant() switch
{
    "list" or "ls" => await Commands.RunListAsync(transport, cts.Token),

    "monitor" or "watch" => await Commands.RunMonitorAsync(transport, cts.Token,
        dumpFrames: args.Contains("--diag") || args.Contains("-d")),

    "diag" or "raw" => await Commands.RunMonitorAsync(transport, cts.Token, dumpFrames: true),

    "switch" when args.Length == 2 && byte.TryParse(args[1], out var host) =>
        await Commands.RunSwitchAllAsync(transport, host, cts.Token),

    "device" when args.Length == 4
                  && byte.TryParse(args[1], out var slot)
                  && args[2].Equals("switch", StringComparison.OrdinalIgnoreCase)
                  && byte.TryParse(args[3], out var slotHost) =>
        await Commands.RunSwitchSlotAsync(transport, slot, slotHost, cts.Token),

    "help" or "--help" or "-h" => Help(),

    _ => Unknown(args)
};

static int Help()
{
    Commands.PrintUsage();
    return 0;
}

static int Unknown(string[] args)
{
    Console.Error.WriteLine($"Unknown command: {string.Join(' ', args)}");
    Console.Error.WriteLine();
    Commands.PrintUsage();
    return 64;
}
