# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

BoltMate is a **companion** to Logi Options+ — not a replacement, not a competitor. It detects host-switch events on a shared Logitech Bolt receiver and fans the switch out to all other paired devices on every machine in the sync group, so the whole peripheral set follows the user between hosts together.

**Scope is intentionally narrow.** Anything Logi Options+ already does (pairing, unpairing, renaming, per-host friendly-name editing, hotkey binding) is out of scope. We only do the cross-device + cross-machine fan-out that Flow can't.

**Trigger sources, all unified into one fan-out**:

| Source | Detection mechanism |
|---|---|
| Easy-Switch button on keyboard | HID++ feature `0x1B04` (REPROG_CONTROLS_V4) — divert CIDs `0x00D1/D2/D3` and listen for `divertedButtonsEvent` (target host arrives in payload before disconnect) |
| Easy-Switch button on mouse / headset / other Logi+ device | Same as above; CIDs vary slightly on cycle-button devices |
| Mouse Flow (cursor crosses screen edge) | **Snoop Logi+'s own `0x1814 SetCurrentHost` writes** on the management interface, filtered by `swid != ours`. The "echo" the docs warn about IS our Flow detection signal. |
| Cross-machine sync | UDP LAN broadcast / multicast announces local receiver + device state; peers correlate against their own link-state events and fan out matching siblings when a device arrives remotely |

**Why this exists**: Logi Flow only triggers multi-device follow when the mouse crosses a screen edge. If the user taps Easy-Switch on the keyboard, the mouse doesn't follow. Flow's edge-detect is also flaky in practice. This app closes the gap — both within one machine and across the LAN.

**Coexistence with Logi Options+ is mandatory** — design contract, not a nice-to-have. Anything that breaks Logi+ is out of scope.

## Stack and platform decisions (locked)

- **.NET 9**, nullable enabled, implicit usings.
- **UI**: Avalonia 12 tray-first (no main window in v0; `ShutdownMode = OnExplicitShutdown`).
- **Reactive architecture**: Public API surfaces `IObservable<T>` and DynamicData `IObservableCache<TObject, TKey>` exclusively — no CLR `event` declarations. Internal notifications use private `Subject<T>` exposed via `.AsObservable()`. Disposal via `CompositeDisposable`. See [feedback memory](.claude/projects/...) for the why.
- **Logging**: Microsoft.Extensions.Logging interfaces in Core (NullLogger default); Serilog backend in CLI + App with Console + rolling File sinks. Planned analytics: Azure Application Insights (slots in without API churn).
- **HID lib**: HidApi.Net (libhidapi binding) — non-exclusive open on macOS via `hid_darwin_set_open_exclusive(0)`; this is what makes Logi+ coexistence work.
- **Platforms**: Mac + Windows day one, Linux best-effort (Logi Options+ doesn't exist on Linux; Solaar is the analog there and uses `/dev/hidraw` exclusively).
- **Distribution**: sideload only — notarized PKG/DMG on Mac (Apple Dev ID), MSI/exe on Windows. **Not pursuing Mac App Store** (sandbox + USB HID + background agent combo is reliably rejected; Logi Options+ itself isn't in MAS). Microsoft Store possible later via WinRT HID backend but not v1.

## Solution layout

```
BoltMate/
├── BoltMate.Core/        # protocol + Bolt model + Rx surface
│   ├── HidPp/                     # frames, request/reply client, HidPp10 register helpers
│   │   ├── Features/              # 0x0001 IRoot, 0x0003 DeviceInfo, 0x0005 DeviceName,
│   │   │                          # 0x0007 DeviceFriendlyName, 0x1004 UnifiedBattery,
│   │   │                          # 0x1814 ChangeHost, 0x1815 HostsInfo, 0x1B04 ReprogControls
│   │   └── Notifications/         # parsers for 0x41 DJ_PAIRING, divertedButtonsEvent, Flow snoop
│   ├── Bolt/                      # BoltReceiver, PairedDevice (DisplayName resolver),
│   │   │                          # ReceiverManager, ReceiverDetails, PairingBackup, WpidCatalog
│   ├── Switcher/                  # SwitcherService (fan-out orchestrator)
│   ├── Topology/                  # UDP/mDNS+TCP cross-machine sync, correlator
│   ├── AppPaths.cs                # per-platform on-disk locations
│   └── AppSettings.cs             # JSON config schema
├── BoltMate.Cli/         # headless service / diagnostic CLI (`boltmate` binary)
├── BoltMate.App/         # Avalonia 12 tray app
│   ├── Assets/                    # tray-icon.png (+@2x), app-icon-512.png, BoltMate.icns
│   ├── App.axaml(.cs)             # tray shell + bootstrap
│   ├── TrayMenuController.cs      # dynamic menu bound to manager.Receivers cache
│   ├── DeviceEnricher.cs          # background metadata reads on link-up
│   ├── SettingsWindow.axaml(.cs)  # receivers list, topology matrix, Status tab, Network tab
│   ├── MacActivationPolicy.cs     # NSApp setActivationPolicy P/Invoke (Dock show/hide)
│   └── AppLoggerSetup.cs          # Serilog logger factory (mirrors CLI's setup)
└── BoltMate.Tests/       # xUnit (90+ tests), FakeReceiverConnection/Transport doubles
```

`Directory.Build.targets` stages libhidapi alongside every project's output and publish bundle, and wraps publish output in a macOS `.app` bundle for the App project.
`Directory.Build.props` (gitignored, per-developer) sets `HidApiWindowsPath` for Windows builds.
`nuget.config` pins restore to nuget.org only.
`version.json` (Nerdbank.GitVersioning) auto-stamps assembly versions from git tags.

## Reactive style

Architectural rule across the codebase: **no CLR events on the public Core API.** State is observable via `IObservable<T>` (point events) or DynamicData's `IObservableCache<TObject, TKey>` (collections). Subjects stay private; only `.AsObservable()` is exposed. `CompositeDisposable` everywhere instead of per-field `Dispose` calls. See [feedback-dotnet-reactive-style](.claude/...) memory.

## Topology-aware fan-out

`SwitcherService` is **manager-scoped** (one per `ReceiverManager`, not per receiver). Routes switch events across all attached receivers by matching host identifiers through each device's `HostBindings`:

1. Origin device pressed Easy-Switch to its host slot N → `targetHostId = device.HostBindings[N].HostIdentifierKey`
2. For each device on every receiver, look up the slot whose binding points to `targetHostId` → `CHANGE_HOST(matching_slot)`
3. Skip the originator, devices without `CanReceiveHostSwitch`, offline devices, and siblings without a matching binding (logged + UI-surfaceable)

Falls back to legacy "same host index for every sibling" routing when the origin's `HostBindings` aren't populated yet (cold-start before `DeviceEnricher` has read them).

`HostBindings` are read from HID++ 2.0 feature `0x1815 HOSTS_INFO` fn 0x10 on every link-up by `DeviceEnricher`. Persisted to disk in `AppSettings.CachedHostBindings` for offline-device bindings to survive restarts.

## App layer composition (BoltMate.App)

- `App.axaml.cs` — bootstrap: settings load, transport, manager, switcher, enricher, topology service, tray controller.
- `DeviceEnricher` — background metadata reads on link-up: feature discovery + name/serial/battery/host bindings.
- `TrayMenuController` — builds dynamic NativeMenu from manager cache; per-receiver sub-sections with per-slot "Switch to Host N" submenus.
- `SettingsWindow` — tabs: General, Receivers (read-only list), Topology (cross-receiver matrix), Network (LAN sync toggle), Status (live local + peer view), Updates.
- `MacActivationPolicy` — NSApp activation policy P/Invoke to show/hide the Dock icon when settings open.

## Build / run

From `BoltMate/` (the solution directory):

```sh
dotnet restore
dotnet build
dotnet test
dotnet run --project BoltMate.Cli/BoltMate.Cli.csproj -- monitor
```

Useful CLI invocations:

```
boltmate list                            # receivers + paired devices
boltmate monitor [--diag] [--verbose]    # listen + fan out (hot-plug aware)
boltmate switch <host>                   # switch ALL paired devices, host 0..2
boltmate device <slot> switch <host>     # switch one slot
boltmate diag                            # monitor + raw frame dump
boltmate service install|uninstall|status # macOS launchd / Windows Task Scheduler autostart
```

## Cross-platform dev pipe

- **Windows VM**: `ssh boltmate-win` (Parallels Win 11 arm64). dotnet 9 + git + libhidapi x64 pre-installed. Repo lives at `C:\dev\LogiPlusXSwitcher`. See reference memory for full setup details.
- **GitHub Actions**: `.github/workflows/ci.yml` runs on self-hosted Mac + Windows runners (labels `self-hosted` + `macOS`/`Windows`). `release.yml` builds self-contained binaries on tag `v*` and uploads to a draft GitHub Release.

## HID++ protocol cheat sheet

Bolt receiver: VID `0x046D`, PID `0xC548`. Management interface = UsagePage `0xFF00`, Usage `0x0001`. Multiple HID interfaces enumerate; only that one carries HID++ traffic.

**Report IDs**:
- `0x10` — short HID++ (7 bytes total)
- `0x11` — long HID++ (20 bytes total)
- `0x20` — DJ (HID++ 1.0 legacy notifications)

**Frame layout** (HID++ 2.0): `report_id, device_index, feature_index, function|sw_id, params...`
- `device_index`: `0xFF` = receiver, `1..6` = paired device slot
- `function|sw_id`: high nibble = function, low nibble = sw_id (software-assigned, used to correlate request/reply)

**Pick a unique sw_id for this app** (`HidPpConstants.OurSwId = 0x0E`) and filter incoming events by `swid != ours` to distinguish:
- Logi Options+ writes (these are our Flow detection signal — keep them)
- Our own writes echoing back (filter these out)

**Receiver enable sequence** (must send on attach or notifications are suppressed — see `HidPp10.cs`):
- `10 FF 80 00 00 09 00` — enable HID++ notifications
- `10 FF 80 02 02 00 00` — enumerate paired devices

**Key sub-IDs (HID++ 1.0 / DJ)**:
- `0x40` CONNECT_DISCONNECT — slot unpaired (address `0x02`); also used to **unpair** programmatically.
- `0x41` DJ_PAIRING — link state change. `data[0] & 0x40` set = link lost, clear = link up. `address == 0x10` = Bolt encrypted link. `data[1..2]` LE = WPID.

**Key features (HID++ 2.0)** — feature indices are device-specific, **always resolve via IRoot `0x0001` getFeature, never hardcode**:
- `0x0001` IRoot — get feature index by feature ID. `RootService.GetFeatureAsync`.
- `0x0003` DEVICE_INFO — read serial number (fn 0x2). `DeviceInfoService.GetSerialAsync`.
- `0x0005` DEVICE_NAME — read product name in chunks (fn 0x0 + fn 0x1). `DeviceNameService`.
- `0x0007` DEVICE_FRIENDLY_NAME — user-editable nickname (read fn 0x0/0x1, write fn 0x2). `DeviceFriendlyNameService`. **Note**: write returns success at the wire level but firmware silently ignores it on tested hardware — tracked as #33.
- `0x1004` UNIFIED_BATTERY — battery percent + charging status. `BatteryService.GetStatusAsync`.
- `0x1814` CHANGE_HOST — `read fn=0x00` returns `(numHosts, currentHost)`; `write fn=0x1` SetCurrentHost is fire-and-forget. **No event is emitted on host change.** See `ChangeHostService`.
- `0x1815` HOSTS_INFO — read-only poll for host names / capabilities. See `HostsInfoService`.
- `0x1B04` REPROG_CONTROLS_V4 — Easy-Switch CIDs `0x00D1` (host 1), `0x00D2` (host 2), `0x00D3` (host 3). `getCidInfo` flags: `& 0x20` divertable, `& 0x40` persistently divertable. `setCidReporting` bfield `0x03` = divert valid + divert set. `divertedButtonsEvent` fires on press BEFORE the device-internal host switch executes (~50ms window). See `ReprogControlsService`.

**Bolt-specific pairing registers** (for slot metadata, pair/unpair, rename):
- `BOLT_UNIQUE_ID`, `BOLT_DEVICE_NAME`, `BOLT_PAIRING_INFORMATION`, `BOLT_DEVICE_DISCOVERY`, `BOLT_PAIRING` — see Solaar `receiver.py:484-531`. Wiring tracked in tasks #16–#25.

## Reference projects (the spec, since Logitech's `cpg-docs` is incomplete)

- **Solaar** (`github.com/pwr-Solaar/Solaar`) — authoritative Linux receiver manager, supports Bolt. Key files:
  - `lib/logitech_receiver/notifications.py:137-220` — 0x41/0x42 parsing
  - `lib/logitech_receiver/common.py:713-720` — sub-ID enum
  - `lib/logitech_receiver/receiver.py:484-531` — BoltReceiver, Bolt registers
  - `lib/logitech_receiver/hidpp20.py:2064-2088` — `get_host_names`
  - `lib/logitech_receiver/settings_templates.py:1295-1318` — `ChangeHost` (proves write-only)
  - `lib/logitech_receiver/special_keys.py:232-234` — Host_Switch_Channel CIDs
  - `lib/logitech_receiver/base.py` — `bolt_pair_step` for the pairing state machine
- **CleverSwitch** (`github.com/MikalaiBarysevich/CleverSwitch`) — Python clone of Flow for Bolt/Unifying. Most directly comparable prior art.
- **fwupd Bolt runtime** (`plugins/logitech-hidpp/fu-logitech-hidpp-runtime-bolt.c`) — authoritative Bolt USB framing.
- **marcelhoffs/input-switcher** — write-only side, useful for known-good CHANGE_HOST byte sequences per device.
- **Logitech `cpg-docs`** — official docs repo but mostly empty as of 2026; Solaar remains the de-facto spec.

## Platform gotchas

- **macOS Input Monitoring** required for HID reads. `dotnet run` from a terminal inherits the terminal app's grant. `InputMonitoringPermission.Check()` reports current state; CLI surfaces a clear message when enumeration returns empty. If reports come back empty or device open fails, that's the first check.
- **macOS Logi+ coexistence**: libhidapi defaults to exclusive opens since 0.12 — we call `hid_darwin_set_open_exclusive(0)` at startup (see `HidApiBridge`). Without this, Logi Options+ and us fight for the device.
- **macOS Dock icon**: app is `LSUIElement=true` (menubar-only). `MacActivationPolicy.ShowDockIcon()` / `HideDockIcon()` flip `NSApp.setActivationPolicy` so the Dock icon appears for the duration of the Settings window, then hides on close.
- **Windows**: standard HID API, no special permission. Logi+ coexists fine because Windows HID is shared by default. **Win 11 fresh install defaults network profile to Public** which excludes the SSH firewall rule on the dev VM — set to Private once.
- **Windows long HID writes (#31)**: on Win 11 arm64 + x64 emulation, hid_write fails with `ERROR_INVALID_FUNCTION` (1) for long (0x11) HID++ writes to the Bolt management interface. Short writes succeed. SendFeatureReport also fails. Blocks unpair/clear/pair on Win until root-caused; debug plan in `HidApiReceiverConnection.Write` comment.
- **Windows arm64**: libusb/hidapi GitHub release only ships x64/x86. Build targeting `win-x64` runs under emulation on arm64 Windows. For native arm64 hidapi: vcpkg or build from source.
- **Linux**: `/dev/hidraw*` requires udev rule for non-root access. Don't run alongside Solaar — both hold the hidraw node.

## Working with this project

The full task list (#1–#30) is tracked via TaskList. Phase 1 (headless service with reactive Core) is complete and tested; tasks #14–#25 cover the pairing/management feature expansion that grew during the design conversation. Phase 2 (full Avalonia UX) builds on the existing tray-scaffold App project.
