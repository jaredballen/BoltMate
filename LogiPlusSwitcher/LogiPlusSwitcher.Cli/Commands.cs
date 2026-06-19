using System.Reactive.Linq;
using DynamicData;
using LogiPlusSwitcher.Core;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Hid.Abstractions;
using LogiPlusSwitcher.Hid.IOKit;
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
        Console.WriteLine("  logiplus device <slot> rename <name>  Rename a paired device. Currently rejected by firmware (see #32).");
        Console.WriteLine("  logiplus receiver clear [--yes]       Unpair every device on the first receiver. Destructive.");
        Console.WriteLine("  logiplus backup [path]                Dump all receiver pairings to JSON.");
        Console.WriteLine("  logiplus diagnose [path]              Bundle pairings + logs + system info into a zip.");
        Console.WriteLine("  logiplus diag                       Dump every raw HID++ frame on the wire.");
        Console.WriteLine("  logiplus service install            Register as a background service / login agent.");
        Console.WriteLine("  logiplus service uninstall          Remove the background registration.");
        Console.WriteLine("  logiplus service status             Show service registration status.");
        Console.WriteLine("  logiplus tail [-n<N>] [--app|--cli] [--no-follow]");
        Console.WriteLine("                                      Tail the newest log file (App by default). Survives log roll.");
        Console.WriteLine("  logiplus diag-divert <slot> <cid>   Probe Set/Clear divert on a single CID (engineering tool).");
        Console.WriteLine("  logiplus help                       This message.");
    }

    public static async Task<int> RunListAsync(IReceiverTransport transport, CancellationToken ct)
    {
        var infos = transport.Enumerate();
        if (infos.Count == 0)
        {
            Console.WriteLine("No Bolt receivers found.");
            var permission = InputMonitoringPermission.Check();
            if (permission == InputMonitoringPermission.Status.Denied || permission == InputMonitoringPermission.Status.Unknown)
            {
                Console.WriteLine();
                Console.WriteLine("⚠ macOS Input Monitoring permission is {0}.", permission);
                Console.WriteLine("  System Settings → Privacy & Security → Input Monitoring → enable for the terminal app.");
                Console.WriteLine("  Then rerun this command.");
            }
            else
            {
                Console.WriteLine("Plug in a Bolt receiver (VID 0x046D PID 0xC548) and retry.");
            }
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
                if (device.LinkUp)
                {
                    try
                    {
                        await receiver.DiscoverFeaturesAsync(device.DeviceIndex, ct);

                        if (device.DeviceNameIndex is { } dnIndex)
                        {
                            try
                            {
                                var liveName = await receiver.DeviceName.GetNameAsync(device.DeviceIndex, dnIndex, ct);
                                if (!string.IsNullOrEmpty(liveName))
                                    device.Name = liveName;
                            }
                            catch (HidPpException) { /* skip */ }
                        }
                        if (device.DeviceInfoIndex is { } diIndex)
                        {
                            var serial = await receiver.DeviceInfo.GetSerialAsync(device.DeviceIndex, diIndex, ct);
                            if (serial is not null) device.Serial = serial;
                        }
                        if (device.UnifiedBatteryIndex is { } batIndex)
                        {
                            var battery = await receiver.Battery.GetStatusAsync(device.DeviceIndex, batIndex, ct);
                            if (battery is { } b) device.LastKnownBattery = b;
                        }
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

        // Single manager-scoped switcher handles topology-aware fan-out across
        // all attached receivers.
        using var switcher = new SwitcherService(manager, LoggerFactory.CreateLogger<SwitcherService>());
        using var fanOutSub = switcher.FanOuts.Subscribe(ev =>
            Console.WriteLine($"  ->  [{ev.OriginatingReceiver.Info.Serial}] fan-out host={ev.TargetHost} slot={ev.Target.DeviceIndex} src={ev.Source}"));

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

        // Note: SwitcherService is now manager-scoped (one for the whole app
        // instance, not one per receiver). It's constructed in RunMonitorAsync
        // and subscribed via the FanOuts stream there.

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

    public static async Task<int> RunBackupAsync(IReceiverTransport transport, string? outputPath, CancellationToken ct)
    {
        AppPaths.EnsureDirectories();
        outputPath ??= Path.Combine(AppPaths.BackupsDirectory, $"pairings-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        var backup = new PairingBackup { CapturedAt = DateTimeOffset.Now };

        foreach (var info in transport.Enumerate())
        {
            using var connection = transport.Open(info);
            using var receiver = new BoltReceiver(info, connection, logger: LoggerFactory.CreateLogger<BoltReceiver>());

            using var settled = new ManualResetEventSlim(false);
            using var sub = receiver.LinkEstablished.Subscribe(_ => settled.Set());
            using var lostSub = receiver.LinkLost.Subscribe(_ => settled.Set());
            receiver.Start();
            settled.Wait(TimeSpan.FromMilliseconds(750), ct);

            var details = await receiver.GetReceiverDetailsAsync(ct: ct);
            for (byte s = HidPpConstants.DeviceIndexFirstSlot; s <= HidPpConstants.DeviceIndexLastSlot; s++)
                await receiver.ReadSlotMetadataAsync(s, ct);

            var rb = new ReceiverBackup
            {
                Serial = details.Serial,
                ProductString = info.ProductString,
                FirmwareVersion = details.FirmwareVersionString,
            };
            foreach (var d in receiver.Devices.Items.OrderBy(d => (int)d.DeviceIndex))
            {
                rb.Slots.Add(new SlotBackup
                {
                    DeviceIndex = d.DeviceIndex,
                    Wpid = d.Wpid,
                    Name = d.Name,
                    Serial = d.Serial,
                    BluetoothAddress = d.BluetoothAddress is null ? null
                        : string.Join(":", d.BluetoothAddress.Select(b => b.ToString("X2"))),
                    CurrentHost = d.LastKnownCurrentHost,
                });
            }
            backup.Receivers.Add(rb);
        }

        await PairingBackup.SaveAsync(backup, outputPath, ct);
        Console.WriteLine($"Wrote backup of {backup.Receivers.Count} receiver(s) to {outputPath}");
        return 0;
    }

    public static async Task<int> RunDiagnoseAsync(IReceiverTransport transport, string? outputPath, CancellationToken ct)
    {
        AppPaths.EnsureDirectories();
        outputPath ??= Path.Combine(AppPaths.BackupsDirectory, $"diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        var tmpDir = Directory.CreateTempSubdirectory("logiplus-diag-");
        try
        {
            // 1. Pairings + receiver metadata snapshot
            var backupJson = Path.Combine(tmpDir.FullName, "pairings.json");
            await RunBackupAsync(transport, backupJson, ct);

            // 2. Last log files
            var logsDir = AppPaths.LogsDirectory;
            if (Directory.Exists(logsDir))
            {
                var diagLogs = Path.Combine(tmpDir.FullName, "logs");
                Directory.CreateDirectory(diagLogs);
                foreach (var f in Directory.EnumerateFiles(logsDir).OrderByDescending(File.GetLastWriteTime).Take(5))
                    File.Copy(f, Path.Combine(diagLogs, Path.GetFileName(f)), overwrite: true);
            }

            // 3. System info text
            var sysInfo = $"""
                LogiPlusSwitcher diagnostic bundle
                Captured: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}
                OS:       {Environment.OSVersion}
                Arch:     {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}
                .NET:     {Environment.Version}
                hidapi:   {HidApi.Hid.VersionString()}
                """;
            await File.WriteAllTextAsync(Path.Combine(tmpDir.FullName, "system.txt"), sysInfo, ct);

            // 4. Zip
            if (File.Exists(outputPath)) File.Delete(outputPath);
            System.IO.Compression.ZipFile.CreateFromDirectory(tmpDir.FullName, outputPath);
            Console.WriteLine($"Wrote diagnostic bundle to {outputPath}");
            return 0;
        }
        finally
        {
            try { tmpDir.Delete(recursive: true); } catch { /* swallow */ }
        }
    }

    public static async Task<int> RunClearReceiverAsync(IReceiverTransport transport, CancellationToken ct, int receiverIndex = 0, bool assumeYes = false)
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

        // Read every slot's metadata first so the confirmation prompt shows real names.
        for (byte s = HidPpConstants.DeviceIndexFirstSlot; s <= HidPpConstants.DeviceIndexLastSlot; s++)
            await receiver.ReadSlotMetadataAsync(s, ct);

        Console.WriteLine($"About to unpair ALL devices on receiver {info.ProductString} (serial {info.Serial}):");
        foreach (var d in receiver.Devices.Items.OrderBy(d => (int)d.DeviceIndex))
            Console.WriteLine($"  slot {d.DeviceIndex}: \"{d.Name ?? "?"}\" wpid=0x{d.Wpid:X4}");

        if (!assumeYes)
        {
            Console.Write("Type YES to confirm: ");
            var answer = Console.ReadLine();
            if (!string.Equals(answer, "YES", StringComparison.Ordinal))
            {
                Console.WriteLine("Cancelled.");
                return 0;
            }
        }

        var cleared = await receiver.ClearAllPairingsAsync(ct: ct);
        Console.WriteLine($"Cleared {cleared} slot(s).");
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

        // Discover features so RenameDeviceAsync can pick the DEVICE_FRIENDLY_NAME (0x0007) path.
        try { await receiver.DiscoverFeaturesAsync(slot, ct); }
        catch (Exception ex) { Console.Error.WriteLine($"  feature discovery failed: {ex.Message}"); }

        // Read current name first so we have something to display.
        var oldName = await receiver.ReadSlotNameAsync(slot, ct) ?? "(unknown)";
        Console.WriteLine($"Renaming slot {slot}: \"{oldName}\" -> \"{newName}\"");
        try
        {
            var ok = await receiver.RenameDeviceAsync(slot, newName, ct: ct);
            if (!ok) { Console.WriteLine("  timed out — name may or may not have been written."); return 2; }

            // Try friendly-name read-back (feature 0x0007) first; fall back to BOLT_DEVICE_NAME.
            string? readBack = null;
            var d = receiver.TryGetDevice(slot);
            if (d?.DeviceFriendlyNameIndex is { } fIdx)
            {
                try { readBack = await receiver.DeviceFriendlyName.GetAsync(slot, fIdx, ct); }
                catch (HidPpException) { /* fall through */ }
            }
            readBack ??= await receiver.ReadSlotNameAsync(slot, ct);
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

    /// <summary>
    /// Probes the read-modify-readback behaviour of <c>SetCidReporting</c> for
    /// a single CID. Diagnostic — verifies whether Normal (0x01) actually
    /// flips divertSet back to 0 on the device, or whether the bug behind
    /// IdentifyAsync's lingering-divert leak is somewhere else.
    /// </summary>
    public static async Task<int> RunDiagDivertAsync(IReceiverTransport transport, byte slot, ushort cid, CancellationToken ct)
    {
        // We already know divert (0x03) works. This sweep finds which clear
        // bfield (if any) restores normal operation in-session. After each
        // candidate we tear down the session and verify the button works
        // again — if it doesn't, something deeper is broken and we abort.
        var candidates = new (byte Bfield, string Label)[]
        {
            (0x01, "0x01  (low-bit valid-only)"),
            (0x08, "0x08  (Solaar valid-bit position)"),
            (0x00, "0x00  (all-zero)"),
            (0x02, "0x02  (set-bit only)"),
            (0x10, "0x10  (set-bit Solaar position)"),
        };

        var results = new List<TrialResult>();
        for (var i = 0; i < candidates.Length; i++)
        {
            if (ct.IsCancellationRequested) break;
            var (bfield, label) = candidates[i];
            var r = await RunOneCandidateAsync(transport, slot, cid, bfield, label, trialNumber: i + 1, total: candidates.Length, ct);
            results.Add(r);

            if (!r.RecoveredAfterClose)
            {
                Console.WriteLine();
                Console.WriteLine("================================================================");
                Console.WriteLine($"  ABORT: session close did not restore wheel-mode button.");
                Console.WriteLine($"  Test cannot continue — please power-cycle the mouse if needed.");
                Console.WriteLine("================================================================");
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine("======================================== SUMMARY ========================================");
        Console.WriteLine("Per trial: open → baseline → divert (0x03) → apply clear → test → close → recovery");
        Console.WriteLine();
        Console.WriteLine("┌─────────────────────────────────────┬────────┬──────────┬───────────┬───────────┐");
        Console.WriteLine("│ clear bfield                        │ bfield │ baseline │ after     │ after     │");
        Console.WriteLine("│                                     │        │ (no wr)  │ clear     │ close     │");
        Console.WriteLine("├─────────────────────────────────────┼────────┼──────────┼───────────┼───────────┤");
        foreach (var r in results)
            Console.WriteLine($"│ {r.Label,-35} │   --   │   {(r.BaselineActuated ? "YES" : "NO ")}    │    {(r.ActuatedInSession ? "YES" : "NO ")}    │    {(r.RecoveredAfterClose ? "YES" : "NO ")}    │");
        Console.WriteLine("└─────────────────────────────────────┴────────┴──────────┴───────────┴───────────┘");
        Console.WriteLine();
        Console.WriteLine("Interpretation:");
        Console.WriteLine("  baseline=NO          → HID open + receiver-enable alone breaks the button.");
        Console.WriteLine("  baseline=YES, clear=YES → that clear bfield restores divert in-session.");
        Console.WriteLine("  baseline=YES, clear=NO, recovery=YES → only session-close clears divert.");
        return 0;
    }

    private static async Task<TrialResult> RunOneCandidateAsync(
        IReceiverTransport transport, byte slot, ushort cid, byte clearBfield, string label, int trialNumber, int total, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"════════ Trial {trialNumber}/{total}: clear via {label} ════════");

        bool baselineActuated;
        bool actuatedInSession;

        // --- Open session, baseline-test, divert, apply clear, ask user ---
        {
            var infos = transport.Enumerate();
            if (infos.Count == 0) return new TrialResult(label, false, false, false);

            var info = infos[0];
            using var connection = transport.Open(info);
            using var receiver = new BoltReceiver(info, connection, logger: LoggerFactory.CreateLogger<BoltReceiver>());

            using var settled = new ManualResetEventSlim(false);
            using var linkSub = receiver.LinkEstablished.Subscribe(d => { if (d.DeviceIndex == slot) settled.Set(); });
            receiver.Start();
            settled.Wait(TimeSpan.FromMilliseconds(1500), ct);

            try { await receiver.DiscoverFeaturesAsync(slot, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"  discover failed: {ex.Message}"); return new TrialResult(label, false, false, false); }

            var device = receiver.TryGetDevice(slot);
            if (device?.ReprogControlsIndex is not { } reprogIndex)
            {
                Console.WriteLine($"  slot {slot} doesn't expose REPROG_CONTROLS_V4");
                return new TrialResult(label, false, false, false);
            }

            // 1. BASELINE — session is open, enable + discover ran, NO writes yet.
            //    If wheel mode is broken here, opening the HID handle alone is the culprit.
            Console.WriteLine("  Session open, enable+discover complete, NO divert writes yet.");
            Console.WriteLine("  Press the wheel-mode button once, then press ENTER.");
            await Task.Run(() => Console.ReadLine(), ct);
            baselineActuated = AskYesNo("  Did wheel mode actuate normally? (baseline)");

            // 2. Divert.
            try { await receiver.ReprogControls.SetCidReportingAsync(slot, reprogIndex, cid, 0x03, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"  divert write failed: {ex.Message}"); return new TrialResult(label, baselineActuated, false, false); }

            // 3. Apply candidate clear.
            try { await receiver.ReprogControls.SetCidReportingAsync(slot, reprogIndex, cid, clearBfield, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"  clear write failed: {ex.Message}"); return new TrialResult(label, baselineActuated, false, false); }

            // 4. Test after divert+clear.
            Console.WriteLine($"  Diverted then cleared with 0x{clearBfield:X2}.");
            Console.WriteLine("  Press the wheel-mode button once, then press ENTER.");
            await Task.Run(() => Console.ReadLine(), ct);
            actuatedInSession = AskYesNo("  Did wheel mode actuate normally? (after clear)");
        }
        // ↑ session disposed here (connection + receiver Dispose).

        // --- Session is now closed. Verify the button recovers. ---
        Console.WriteLine();
        Console.WriteLine("  Session torn down. Press the wheel-mode button once, then press ENTER.");
        await Task.Run(() => Console.ReadLine(), ct);
        var recoveredAfterClose = AskYesNo("  Did wheel mode actuate normally? (after close)");

        return new TrialResult(label, baselineActuated, actuatedInSession, recoveredAfterClose);
    }

    private static bool AskYesNo(string prompt)
    {
        while (true)
        {
            Console.Write(prompt + " [y/n] ");
            var line = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (line is "y" or "yes") return true;
            if (line is "n" or "no") return false;
        }
    }

    private sealed record TrialResult(string Label, bool BaselineActuated, bool ActuatedInSession, bool RecoveredAfterClose);

    /// <summary>
    /// Opens the HID handle to the first receiver, sends NO writes (no
    /// enable, no enumerate, nothing), and waits for ENTER. Used to test
    /// whether the bare act of holding the HID handle open is enough to
    /// affect device firmware behaviour (e.g. wheel-mode toggle).
    /// </summary>
    public static async Task<int> RunDiagOpenOnlyAsync(IReceiverTransport transport, CancellationToken ct)
    {
        var infos = transport.Enumerate();
        if (infos.Count == 0) { Console.WriteLine("No Bolt receivers found."); return 1; }

        var info = infos[0];
        Console.WriteLine($"Opening {info.ProductString} ({info.Path}) — NO writes will be sent.");
        using var connection = transport.Open(info);
        Console.WriteLine("Handle open. Press the wheel-mode + thumb buttons now and test.");
        Console.WriteLine("Press ENTER when done to close the handle.");
        await Task.Run(() => Console.ReadLine(), ct);
        Console.WriteLine("Closing handle.");
        return 0;
    }

    /// <summary>
    /// Definitive test: does libhidapi-with-correct-init actually deliver
    /// shared access on this system? Performs the prescribed init sequence
    /// (Hid.Init() FIRST, then hid_darwin_set_open_exclusive(0)) and then
    /// attempts to open the receiver TWICE in the same process. If shared
    /// access is real, both opens succeed and report distinct handles. If
    /// libhidapi is silently still seizing, the second open fails.
    /// </summary>
    public static async Task<int> RunDiagLibhidapiSharedAsync(IReceiverTransport transport, CancellationToken ct)
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
        {
            Console.WriteLine("diag-libhidapi-shared is macOS-only.");
            return 2;
        }

        // (transport already had EnsureNativeLibraryResolver + SetMacOsNonExclusive
        // called during construction in Program.cs, which now uses the prescribed
        // init-first ordering.)

        var infos = HidApi.Hid.Enumerate(0x046D, 0xC548).ToList();
        var managementInfo = infos.FirstOrDefault(i => i.UsagePage == 0xFF00 && i.Usage == 0x0001);
        if (managementInfo is null)
        {
            Console.WriteLine("Could not find Bolt receiver management interface via libhidapi.");
            return 1;
        }

        Console.WriteLine($"Found management interface at path: {managementInfo.Path}");
        Console.WriteLine();

        Console.WriteLine("OPEN #1 (via libhidapi with prescribed init ordering)...");
        HidApi.Device? device1 = null;
        try
        {
            device1 = new HidApi.Device(managementInfo.Path);
            Console.WriteLine("  Open #1 SUCCEEDED");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Open #1 FAILED: {ex.Message}");
            return 3;
        }

        Console.WriteLine();
        Console.WriteLine("OPEN #2 (same process, while #1 is still held)...");
        HidApi.Device? device2 = null;
        try
        {
            device2 = new HidApi.Device(managementInfo.Path);
            Console.WriteLine("  Open #2 SUCCEEDED — shared access IS working at libhidapi layer.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Open #2 FAILED: {ex.Message}");
            Console.WriteLine("  → libhidapi is NOT delivering shared access despite the prescribed init order.");
        }

        Console.WriteLine();
        Console.WriteLine("Holding both handles open. Press wheel-mode + thumb buttons on the mouse.");
        Console.WriteLine("Press ENTER when done.");
        await Task.Run(() => Console.ReadLine(), ct);

        device1?.Dispose();
        device2?.Dispose();
        return 0;
    }

    /// <summary>
    /// Opens the Bolt receiver's management interface directly via IOKit
    /// (bypassing libhidapi) with kIOHIDOptionsTypeNone, holds open until
    /// ENTER. Used to test whether libhidapi's hid_darwin_set_open_exclusive(0)
    /// is actually delivering shared access — or whether IOKit-direct with
    /// explicit None still fixes the wheel-mode break. Mac-only.
    /// </summary>
    public static async Task<int> RunDiagIOKitOpenAsync(CancellationToken ct)
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
        {
            Console.WriteLine("diag-iokit-open is macOS-only.");
            return 2;
        }

        // VID 0x046D (Logitech), PID 0xC548 (Bolt receiver). Filter by
        // UsagePage 0xFF00 + Usage 0x0001 done after enumeration since we
        // get a CFSet of all matching interfaces and pick the management one.
        var manager = LogiPlusSwitcher.Hid.IOKit.IOKitInterop.IOHIDManagerCreate(IntPtr.Zero, 0);
        if (manager == IntPtr.Zero) { Console.Error.WriteLine("IOHIDManagerCreate failed"); return 1; }

        try
        {
            var match = LogiPlusSwitcher.Hid.IOKit.IOKitInterop.CreateMatchingDictionary(0x046D, 0xC548);
            LogiPlusSwitcher.Hid.IOKit.IOKitInterop.IOHIDManagerSetDeviceMatching(manager, match);
            LogiPlusSwitcher.Hid.IOKit.IOKitInterop.CFRelease(match);

            var open = LogiPlusSwitcher.Hid.IOKit.IOKitInterop.IOHIDManagerOpen(manager, LogiPlusSwitcher.Hid.IOKit.IOKitInterop.OptionsNone);
            Console.WriteLine($"IOHIDManagerOpen result = 0x{open:X8}");

            var set = LogiPlusSwitcher.Hid.IOKit.IOKitInterop.IOHIDManagerCopyDevices(manager);
            if (set == IntPtr.Zero) { Console.Error.WriteLine("No matching IOHIDDevices found"); return 1; }

            var count = (int)LogiPlusSwitcher.Hid.IOKit.IOKitInterop.CFSetGetCount(set);
            Console.WriteLine($"Matched {count} HID interface(s) on the Bolt receiver.");
            var devices = new IntPtr[count];
            LogiPlusSwitcher.Hid.IOKit.IOKitInterop.CFSetGetValues(set, devices);

            IntPtr managementDevice = IntPtr.Zero;
            foreach (var dev in devices)
            {
                if (dev == IntPtr.Zero) continue;
                var usagePage = LogiPlusSwitcher.Hid.IOKit.IOKitInterop.GetInt32Property(dev, "PrimaryUsagePage");
                var usage = LogiPlusSwitcher.Hid.IOKit.IOKitInterop.GetInt32Property(dev, "PrimaryUsage");
                Console.WriteLine($"  interface: UsagePage=0x{usagePage:X4} Usage=0x{usage:X4}");
                if (usagePage == 0xFF00 && usage == 0x0001) managementDevice = dev;
            }

            if (managementDevice == IntPtr.Zero)
            {
                Console.Error.WriteLine("Could not find management interface (UsagePage 0xFF00, Usage 0x0001).");
                LogiPlusSwitcher.Hid.IOKit.IOKitInterop.CFRelease(set);
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("Opening management interface via IOHIDDeviceOpen(kIOHIDOptionsTypeNone)...");
            var openResult = LogiPlusSwitcher.Hid.IOKit.IOKitInterop.IOHIDDeviceOpen(
                managementDevice, LogiPlusSwitcher.Hid.IOKit.IOKitInterop.OptionsNone);
            Console.WriteLine($"IOHIDDeviceOpen result = 0x{openResult:X8} ({(openResult == 0 ? "OK" : "FAIL")})");

            if (openResult != 0)
            {
                Console.WriteLine();
                Console.WriteLine("Open failed. Common return values:");
                Console.WriteLine("  0xE00002C5 = kIOReturnExclusiveAccess (someone else has it seized)");
                Console.WriteLine("  0xE00002BC = kIOReturnNotPrivileged   (missing Input Monitoring permission)");
                LogiPlusSwitcher.Hid.IOKit.IOKitInterop.CFRelease(set);
                return 3;
            }

            Console.WriteLine();
            Console.WriteLine("Management interface open via IOKit-direct.");
            Console.WriteLine("Press wheel-mode + thumb buttons on the mouse now and test.");
            Console.WriteLine("Press ENTER when done to close.");
            await Task.Run(() => Console.ReadLine(), ct);

            LogiPlusSwitcher.Hid.IOKit.IOKitInterop.IOHIDDeviceClose(managementDevice, LogiPlusSwitcher.Hid.IOKit.IOKitInterop.OptionsNone);
            LogiPlusSwitcher.Hid.IOKit.IOKitInterop.CFRelease(set);
            Console.WriteLine("Closed.");
            return 0;
        }
        finally
        {
            LogiPlusSwitcher.Hid.IOKit.IOKitInterop.IOHIDManagerClose(manager, LogiPlusSwitcher.Hid.IOKit.IOKitInterop.OptionsNone);
            LogiPlusSwitcher.Hid.IOKit.IOKitInterop.CFRelease(manager);
        }
    }

    /// <summary>
    /// Opens, sends enable+enumerate (so we can discover features), then sends
    /// bfield 0x02 (clear/re-arm) to every divertable CID on the given slot,
    /// then waits for ENTER. Tests whether a bulk "re-arm" handshake restores
    /// device-firmware button handling that the bare open broke.
    /// </summary>
    public static async Task<int> RunDiagOpenAndRearmAsync(IReceiverTransport transport, byte slot, CancellationToken ct)
    {
        var infos = transport.Enumerate();
        if (infos.Count == 0) { Console.WriteLine("No Bolt receivers found."); return 1; }

        var info = infos[0];
        using var connection = transport.Open(info);
        using var receiver = new BoltReceiver(info, connection, logger: LoggerFactory.CreateLogger<BoltReceiver>());

        using var settled = new ManualResetEventSlim(false);
        using var linkSub = receiver.LinkEstablished.Subscribe(d => { if (d.DeviceIndex == slot) settled.Set(); });
        receiver.Start();
        settled.Wait(TimeSpan.FromMilliseconds(1500), ct);

        try { await receiver.DiscoverFeaturesAsync(slot, ct); }
        catch (Exception ex) { Console.Error.WriteLine($"discover failed: {ex.Message}"); return 3; }

        var device = receiver.TryGetDevice(slot);
        if (device?.ReprogControlsIndex is not { } reprogIndex)
        {
            Console.WriteLine($"slot {slot} doesn't expose REPROG_CONTROLS_V4");
            return 2;
        }

        var controls = await receiver.ReprogControls.ListControlsAsync(slot, reprogIndex, ct);
        var divertable = controls.Where(c => c.IsDivertable).ToList();
        Console.WriteLine($"Re-arming {divertable.Count} divertable CID(s) on slot {slot}…");
        foreach (var c in divertable)
        {
            try
            {
                await receiver.ReprogControls.SetCidReportingAsync(slot, reprogIndex, c.ControlId, 0x02, ct);
                Console.WriteLine($"  re-armed 0x{c.ControlId:X4}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  0x{c.ControlId:X4} failed: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Handle still open. Press the wheel-mode + thumb buttons now and test.");
        Console.WriteLine("Press ENTER when done to close.");
        await Task.Run(() => Console.ReadLine(), ct);
        return 0;
    }

    /// <summary>
    /// Pure read-only snapshot: enumerates every CID on the given slot via
    /// getCidInfo, then reads getCidReporting for each, printing flags +
    /// divert state. Also reads the receiver-level HID++ 1.0 notification
    /// register (0x00). Used to diff "before App" vs "during App" vs "after
    /// App" — diff tells us what state changes when our app opens.
    /// </summary>
    public static async Task<int> RunDiagSnapshotAsync(IReceiverTransport transport, byte slot, CancellationToken ct)
    {
        var infos = transport.Enumerate();
        if (infos.Count == 0) { Console.WriteLine("No Bolt receivers found."); return 1; }

        var info = infos[0];
        using var connection = transport.Open(info);
        using var receiver = new BoltReceiver(info, connection, logger: LoggerFactory.CreateLogger<BoltReceiver>());

        using var settled = new ManualResetEventSlim(false);
        using var linkSub = receiver.LinkEstablished.Subscribe(d => { if (d.DeviceIndex == slot) settled.Set(); });
        receiver.Start();
        settled.Wait(TimeSpan.FromMilliseconds(1500), ct);

        try { await receiver.DiscoverFeaturesAsync(slot, ct); }
        catch (Exception ex) { Console.Error.WriteLine($"discover failed: {ex.Message}"); return 3; }

        var device = receiver.TryGetDevice(slot);
        if (device?.ReprogControlsIndex is not { } reprogIndex)
        {
            Console.WriteLine($"slot {slot} doesn't expose REPROG_CONTROLS_V4 (0x1B04).");
            return 2;
        }

        Console.WriteLine($"=== diag-snapshot: slot {slot}, reprog feature 0x{reprogIndex:X2} ===");
        Console.WriteLine($"=== timestamp (utc): {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} ===");
        Console.WriteLine();

        // Reading register 0x00 lets us see if our enable-write changed the
        // receiver-wide notification flags compared to what Logi+ had set.
        try
        {
            var notifFlags = await receiver.ReadShortReceiverRegisterAsync(0x00, ct);
            Console.WriteLine($"  Receiver notification register (0x00): bytes=[{BitConverter.ToString(notifFlags).Replace("-", " ")}]");
        }
        catch (Exception ex) { Console.WriteLine($"  Notification register read failed: {ex.Message}"); }
        Console.WriteLine();

        var controls = await receiver.ReprogControls.ListControlsAsync(slot, reprogIndex, ct);
        Console.WriteLine($"  {controls.Count} CIDs in control table:");
        Console.WriteLine();
        Console.WriteLine("  ┌──────┬───────────────────────────────────────┬────────┬──────────┬─────────┬──────────┐");
        Console.WriteLine("  │ cid  │ flags                                 │ bfield │ divert?  │ persist?│ remap    │");
        Console.WriteLine("  ├──────┼───────────────────────────────────────┼────────┼──────────┼─────────┼──────────┤");
        foreach (var c in controls)
        {
            try
            {
                var st = await receiver.ReprogControls.GetCidReportingAsync(slot, reprogIndex, c.ControlId, ct);
                Console.WriteLine($"  │0x{c.ControlId:X4}│ {c.Flags,-37} │  0x{st.Bfield:X2}  │   {(st.DivertSet ? "YES" : "NO ")}    │   {(st.PersistDivertSet ? "YES" : "NO ")}   │ 0x{st.RemapControlId:X4}   │");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  │0x{c.ControlId:X4}│ READ FAILED: {ex.Message}");
            }
        }
        Console.WriteLine("  └──────┴───────────────────────────────────────┴────────┴──────────┴─────────┴──────────┘");
        return 0;
    }

    /// <summary>
    /// Tails the newest LogiPlusSwitcher log file matching the given prefix.
    /// App writes logiplus-app-*.log; CLI writes logiplus-*.log. Both land
    /// in <see cref="AppPaths.LogsDirectory"/>, so one tail can target either
    /// (or both) by prefix glob.
    /// </summary>
    /// <param name="prefix">File prefix glob, e.g. "logiplus-app-*.log" for
    /// the App, "logiplus-*.log" for everything. Defaults to the App if any
    /// app-prefixed file exists, else falls back to all .log files.</param>
    public static async Task<int> RunTailAsync(int lastN, bool follow, string? prefix, CancellationToken ct)
    {
        var dir = AppPaths.LogsDirectory;
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"Logs directory does not exist: {dir}");
            return 1;
        }

        string ResolvePrefix()
        {
            if (!string.IsNullOrEmpty(prefix)) return prefix!;
            var hasAppLog = new DirectoryInfo(dir).GetFiles("logiplus-app-*.log").Any();
            return hasAppLog ? "logiplus-app-*.log" : "*.log";
        }

        var glob = ResolvePrefix();
        var newest = new DirectoryInfo(dir).GetFiles(glob)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (newest is null)
        {
            Console.Error.WriteLine($"No {glob} files in {dir}");
            return 1;
        }

        Console.Error.WriteLine($"==> {newest.FullName} <==");

        await using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        if (lastN > 0)
        {
            var all = new List<string>();
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is not null) all.Add(line);
            }
            var skip = Math.Max(0, all.Count - lastN);
            for (var i = skip; i < all.Count; i++)
                Console.WriteLine(all[i]);
        }
        else
        {
            stream.Seek(0, SeekOrigin.End);
        }

        if (!follow) return 0;

        var currentPath = newest.FullName;
        while (!ct.IsCancellationRequested)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
                Console.WriteLine(line);

            await Task.Delay(250, ct);

            var latestNow = new DirectoryInfo(dir).GetFiles(glob)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latestNow is not null && latestNow.FullName != currentPath)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"==> {latestNow.FullName} <==");
                reader.Dispose();
                stream.Dispose();
                return await RunTailAsync(0, follow: true, prefix, ct);
            }
        }
        return 0;
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
