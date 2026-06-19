using LogiPlusSwitcher.Cli;
using LogiPlusSwitcher.Core.Hid;
using Microsoft.Extensions.Logging;

var verbose = args.Contains("--verbose") || args.Contains("-v");
args = args.Where(a => a is not ("--verbose" or "-v")).ToArray();

using var loggerFactory = LoggerSetup.Create(verbose ? LogLevel.Debug : LogLevel.Information);

HidApiBridge.EnsureNativeLibraryResolver();
HidApiBridge.SetMacOsNonExclusive();

var transport = new HidApiReceiverTransport(loggerFactory);
Commands.LoggerFactory = loggerFactory;
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

    "device" when args.Length == 3
                  && byte.TryParse(args[1], out var unpairSlot)
                  && args[2].Equals("unpair", StringComparison.OrdinalIgnoreCase) =>
        await Commands.RunUnpairSlotAsync(transport, unpairSlot, cts.Token),

    "device" when args.Length == 5
                  && args[1].Equals("--receiver", StringComparison.OrdinalIgnoreCase)
                  && int.TryParse(args[2], out var rIdx)
                  && byte.TryParse(args[3], out var unpairSlot2)
                  && args[4].Equals("unpair", StringComparison.OrdinalIgnoreCase) =>
        await Commands.RunUnpairSlotAsync(transport, unpairSlot2, cts.Token, rIdx),

    "service" when args.Length == 2 && args[1].Equals("install", StringComparison.OrdinalIgnoreCase)
        => ServiceCommands.Install(),
    "service" when args.Length == 2 && args[1].Equals("uninstall", StringComparison.OrdinalIgnoreCase)
        => ServiceCommands.Uninstall(),
    "service" when args.Length == 2 && args[1].Equals("status", StringComparison.OrdinalIgnoreCase)
        => ServiceCommands.Status(),

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
