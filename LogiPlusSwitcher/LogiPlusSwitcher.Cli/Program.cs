using LogiPlusSwitcher.Cli;
using LogiPlusSwitcher.Hid.Abstractions;
using Microsoft.Extensions.Logging;

var verbose = args.Contains("--verbose") || args.Contains("-v");
args = args.Where(a => a is not ("--verbose" or "-v")).ToArray();

using var loggerFactory = LoggerSetup.Create(verbose ? LogLevel.Debug : LogLevel.Information);

// Composition root for the HID transport. macOS uses IOKit-direct (libhidapi
// 0.15.0 ignores shared-access flag); Win/Linux use the libhidapi-backed
// transport.
IReceiverTransport transport = OperatingSystem.IsMacOS()
    ? new LogiPlusSwitcher.Hid.IOKit.IOKitReceiverTransport(loggerFactory)
    : new LogiPlusSwitcher.Hid.HidApi.HidApiReceiverTransport(loggerFactory);
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

    "device" when args.Length == 4
                  && byte.TryParse(args[1], out var renSlot)
                  && args[2].Equals("rename", StringComparison.OrdinalIgnoreCase) =>
        await Commands.RunRenameSlotAsync(transport, renSlot, args[3], cts.Token),

    "receiver" when args.Length >= 2 && args[1].Equals("clear", StringComparison.OrdinalIgnoreCase) =>
        await Commands.RunClearReceiverAsync(transport, cts.Token, assumeYes: args.Contains("--yes") || args.Contains("-y")),

    "backup" => await Commands.RunBackupAsync(transport,
        args.Length >= 2 ? args[1] : null, cts.Token),

    "diagnose" => await Commands.RunDiagnoseAsync(transport,
        args.Length >= 2 ? args[1] : null, cts.Token),

    "device" when args.Length == 5
                  && args[1].Equals("--receiver", StringComparison.OrdinalIgnoreCase)
                  && int.TryParse(args[2], out var rIdx)
                  && byte.TryParse(args[3], out var unpairSlot2)
                  && args[4].Equals("unpair", StringComparison.OrdinalIgnoreCase) =>
        await Commands.RunUnpairSlotAsync(transport, unpairSlot2, cts.Token, rIdx),

    "diag-divert" when args.Length == 3
                       && byte.TryParse(args[1], out var ddSlot)
                       && ushort.TryParse(args[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? args[2][2..] : args[2],
                            System.Globalization.NumberStyles.HexNumber, null, out var ddCid) =>
        await Commands.RunDiagDivertAsync(transport, ddSlot, ddCid, cts.Token),

    "diag-snapshot" when args.Length == 2 && byte.TryParse(args[1], out var dsSlot) =>
        await Commands.RunDiagSnapshotAsync(transport, dsSlot, cts.Token),

    "diag-open-only" => await Commands.RunDiagOpenOnlyAsync(transport, cts.Token),

    "diag-rearm" when args.Length == 2 && byte.TryParse(args[1], out var drSlot) =>
        await Commands.RunDiagOpenAndRearmAsync(transport, drSlot, cts.Token),

    "diag-iokit-open" => await Commands.RunDiagIOKitOpenAsync(cts.Token),

    "diag-libhidapi-shared" => await Commands.RunDiagLibhidapiSharedAsync(transport, cts.Token),

    "dump-features" when args.Length == 2 && byte.TryParse(args[1], out var dfSlot) =>
        await Commands.RunDumpFeaturesAsync(transport, dfSlot, cts.Token),

    "sniff-all" => await Commands.RunSniffAllInterfacesAsync(cts.Token),

    "diag-force-divert" when args.Length == 4
                              && byte.TryParse(args[1], out var fdSlot)
                              && byte.TryParse(args[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? args[2][2..] : args[2],
                                   System.Globalization.NumberStyles.HexNumber, null, out var fdReprog)
                              && ushort.TryParse(args[3].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? args[3][2..] : args[3],
                                   System.Globalization.NumberStyles.HexNumber, null, out var fdCid) =>
        await Commands.RunDiagForceDivertAsync(transport, fdSlot, fdReprog, fdCid, cts.Token),

    "tail" => await Commands.RunTailAsync(
        lastN: args.Skip(1).FirstOrDefault(a => a.StartsWith("-n", StringComparison.Ordinal)) is { } n
                && int.TryParse(n.AsSpan(2), out var nv) ? nv : 50,
        follow: !args.Contains("--no-follow"),
        prefix: args.Contains("--app") ? "logiplus-app-*.log"
              : args.Contains("--cli") ? "logiplus-*.log"
              : null,
        cts.Token),

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
