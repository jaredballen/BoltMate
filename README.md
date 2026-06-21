# BoltMate

A **companion** to Logi Options+ — not a replacement, not a competitor.

When you switch your keyboard, mouse, headset, or other Bolt-paired Logitech device to another host (via the Easy-Switch button, the device's channel button, or Mouse Flow), BoltMate detects the event and fans the host change out to every other device paired to the same Bolt receiver. The whole peripheral set follows you between computers together.

**Why this exists.** Logi Flow only triggers a multi-device follow when the mouse cursor crosses a screen edge. If you tap Easy-Switch on the keyboard, the mouse doesn't follow. Flow's edge-detect is also flaky in practice. This app closes that gap.

**Status: alpha.** Headless service is feature-complete and hardware-verified on macOS for the core flow + management features (receiver info, device identification, unpair, clear, backup, tap-to-identify, battery). A tray UI (Avalonia 12) ships in scaffold form and will mature in phase 2.

## What it can do

### Free tier (read + runtime fan-out)

- **Hot-plug multi-receiver detection** — attach/detach handled automatically.
- **Live device enumeration** — model name, serial, BLE address, firmware version, battery level, paired host.
- **Unified fan-out switching** — any Easy-Switch press, mouse Flow event, or in-app hotkey makes every other paired device follow to the same host.
- **Tap-to-identify** — flash a slot's controls for 5 seconds to confirm which physical device is which.
- **Backup pairings to JSON** — capture your receiver tables for support flows or future restore.
- **Diagnostic bundle** — `boltmate diagnose` zips pairings + recent logs + system info for support.

### Pro tier (write-to-receiver-flash)

- **Unpair** a single slot.
- **Clear all** pairings on a receiver.
- **Pair new devices** (state machine implemented, passkey UI in phase 2).
- **Rename** a paired device (implementation lands once we wire HID++ 2.0 feature 0x0005 setName — current path is firmware-blocked).
- **Move pairings between receivers** (consolidate two receivers into one without re-pairing every device manually).

Tier enforcement happens at the App layer; Core has no `if (paid)` branches — see the [tier-gating design memo](docs/tier-gating.md) when it exists.

## Trigger sources (all unified into one fan-out)

| Source | Detection mechanism |
|---|---|
| Easy-Switch button on keyboard | HID++ feature `0x1B04` — divert CIDs `0x00D1/D2/D3`, listen for `divertedButtonsEvent` (target host arrives before disconnect) |
| Easy-Switch on mouse / headset / other Logi+ device | Same mechanism |
| Mouse Flow (cursor crosses screen edge) | Snoop Logi+'s own `0x1814 SetCurrentHost` writes on the management interface |
| In-app hotkey | `boltmate switch <host>` |

## Requirements

- **.NET 9 SDK** to build.
- **libhidapi** 0.15+. macOS: `brew install hidapi`. Windows: `vcpkg install hidapi`, or extract `hidapi.dll` from the [libusb/hidapi release](https://github.com/libusb/hidapi/releases) and either drop it into `C:\dev\hidapi-win\x64\` or point `HidApiWindowsPath` at it in `Directory.Build.props`. Bundled into self-contained publish output.
- **macOS Input Monitoring permission** for the terminal (or app) that runs the binary. System Settings → Privacy & Security → Input Monitoring.
- **Logi Options+ may keep running** — coexistence is mandatory. macOS HID open is non-exclusive via `hid_darwin_set_open_exclusive(0)`.

## Build

```sh
cd src
dotnet restore
dotnet build
dotnet test
```

## Run (dev)

```sh
dotnet run --project BoltMate.Cli/BoltMate.Cli.csproj -- <command>
```

Commands:

```
boltmate list                            # receivers + paired devices + live identity
boltmate monitor [--diag] [--verbose]    # listen + fan out (hot-plug aware)
boltmate switch <host>                   # switch ALL paired devices to host 0..2
boltmate device <slot> switch <host>     # switch one slot
boltmate device <slot> unpair            # destructive — removes the pairing
boltmate device <slot> rename <name>     # currently firmware-blocked; see issue #32
boltmate receiver clear [--yes]          # destructive — unpair every device on receiver 0
boltmate backup [path]                   # write all pairings as JSON
boltmate diagnose [path]                 # zip pairings + logs + system info for support
boltmate diag                            # monitor + raw frame dump
boltmate service install|uninstall|status # launchd / Task Scheduler autostart
boltmate help
```

The `monitor` command survives unplug/replug and handles multiple receivers in parallel.

## Distribute (self-contained sideload)

```sh
# macOS Apple Silicon
dotnet publish BoltMate.Cli/BoltMate.Cli.csproj \
  -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true

# macOS Intel
dotnet publish BoltMate.Cli/BoltMate.Cli.csproj \
  -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true

# Windows x64 (also runs under arm64 emulation on Win-on-ARM)
dotnet publish BoltMate.Cli/BoltMate.Cli.csproj \
  -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Output: `BoltMate.Cli/bin/Release/net9.0/<rid>/publish/` contains a single `boltmate` binary (.NET runtime bundled) plus `libhidapi.dylib` / `hidapi.dll`.

CI for self-hosted Mac + Windows runners is in `.github/workflows/ci.yml`; releases via tag push trigger `release.yml`.

## Solution layout

```
src/
├── BoltMate.Core/        # protocol + Bolt model + Rx surface
│   ├── Hid/                       # transport (libhidapi) + connection
│   ├── HidPp/                     # frame primitives, request/reply client
│   │   ├── Features/              # one service per HID++ 2.0 feature ID
│   │   └── Notifications/         # parsers for 0x41, divertedButtonsEvent, Flow snoop
│   ├── Bolt/                      # BoltReceiver, ReceiverManager, PairedDevice, PairingBackup
│   └── Switcher/                  # SwitcherService (fan-out orchestrator)
├── BoltMate.Cli/         # headless service / diagnostic CLI
├── BoltMate.App/         # Avalonia 12 tray scaffold (phase 2)
└── BoltMate.Tests/       # xUnit + fakes
```

## Protocol references

Logitech's `cpg-docs` is mostly empty as of 2026, so the open-source community is the de-facto spec.

- [Solaar](https://github.com/pwr-Solaar/Solaar) — canonical Linux receiver manager. The HID++ 1.0 sub-IDs, 2.0 feature definitions, and Bolt-specific registers all come from here.
- [CleverSwitch](https://github.com/MikalaiBarysevich/CleverSwitch) — Python clone of Mouse Flow for Bolt/Unifying. Closest prior art.
- [fwupd logitech-hidpp](https://github.com/fwupd/fwupd/tree/main/plugins/logitech-hidpp) — authoritative Bolt USB framing.
- [marcelhoffs/input-switcher](https://github.com/marcelhoffs/input-switcher) — known-good CHANGE_HOST byte sequences per device.

## License

MIT (see `LICENSE`).
