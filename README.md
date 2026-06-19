# LogiPlusSwitcher

A **companion** to Logi Options+ — not a replacement, not a competitor.

When you switch your keyboard, mouse, headset, or other Bolt-paired Logitech
device to another host (via the Easy-Switch button, the device's channel
button, or Mouse Flow), LogiPlusSwitcher detects the event and fans the host
change out to every other device paired to the same Bolt receiver. The whole
peripheral set follows you between computers together.

**Why this exists.** Logi Flow only triggers a multi-device follow when the
mouse cursor crosses a screen edge. If you tap Easy-Switch on the keyboard,
the mouse doesn't follow. Flow's edge-detect is also flaky in practice. This
app closes that gap.

**Status: alpha.** Headless service is feature-complete; hardware verification
is in progress. A tray UI (Avalonia 11) lands in phase 2.

## What it detects

| Trigger | How |
|---|---|
| Easy-Switch button on a keyboard | HID++ feature `0x1B04` — diverts CIDs `0x00D1/D2/D3` and listens for `divertedButtonsEvent` (target host arrives in payload before the device disconnects) |
| Easy-Switch / channel button on a mouse or headset | Same mechanism |
| Mouse Flow (cursor crosses screen edge) | Snoops Logi Options+'s own `0x1814 SetCurrentHost` writes on the receiver's management interface |
| In-app hotkey | `logiplus switch <host>` |

In every case the response is the same: write `CHANGE_HOST` (feature `0x1814`)
to every other paired device on the receiver.

## Requirements

- **.NET 9 SDK** to build.
- **libhidapi** (0.15+) at runtime. On macOS: `brew install hidapi`. On
  Windows: `vcpkg install hidapi` or place `hidapi.dll` somewhere the build
  target can find it. Bundled into self-contained publish output.
- **macOS Input Monitoring permission** for the terminal (or app) that runs
  the binary. System Settings → Privacy & Security → Input Monitoring.
- **Logi Options+ may keep running** — coexistence is mandatory. The macOS HID
  open is non-exclusive via `hid_darwin_set_open_exclusive(0)`.

## Build

```sh
cd LogiPlusSwitcher
dotnet restore
dotnet build
dotnet test
```

## Run (dev)

```sh
dotnet run --project LogiPlusSwitcher.Cli/LogiPlusSwitcher.Cli.csproj -- <command>
```

Commands:

```
logiplus list                            # list receivers + paired devices
logiplus monitor                         # listen + fan out (default service mode)
logiplus monitor --diag                  # monitor + dump every HID++ frame
logiplus switch <host>                   # switch ALL paired devices to host 0..2
logiplus device <slot> switch <host>     # switch a single slot 1..6 to host 0..2
logiplus diag                            # alias for `monitor --diag`
logiplus help
```

`monitor` survives unplug/replug and handles multiple receivers in parallel
(if you have a Bolt receiver per host).

## Distribute (self-contained sideload)

```sh
# macOS Apple Silicon
dotnet publish LogiPlusSwitcher.Cli/LogiPlusSwitcher.Cli.csproj \
  -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true

# macOS Intel
dotnet publish LogiPlusSwitcher.Cli/LogiPlusSwitcher.Cli.csproj \
  -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true

# Windows x64
dotnet publish LogiPlusSwitcher.Cli/LogiPlusSwitcher.Cli.csproj \
  -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Output: `LogiPlusSwitcher.Cli/bin/Release/net9.0/<rid>/publish/` contains a
single `logiplus` binary (.NET runtime bundled) plus `libhidapi.dylib` /
`hidapi.dll` next to it. The Mac binary needs notarisation for Gatekeeper-
friendly distribution; the Windows binary needs an Authenticode signature
for SmartScreen-friendly distribution.

## Solution layout

```
LogiPlusSwitcher/
├── LogiPlusSwitcher.Core/        # HID transport, HID++ protocol, device model
│   ├── Hid/                       # transport abstraction + libhidapi impl
│   ├── HidPp/                     # frames, client, features, notifications
│   ├── Bolt/                      # receiver model, paired device, manager
│   └── Switcher/                  # orchestrator
├── LogiPlusSwitcher.Cli/         # headless service / diagnostic CLI
├── LogiPlusSwitcher.Tests/       # xUnit tests against fake transport
├── Directory.Build.targets       # stages libhidapi into bin/ and publish/
└── nuget.config                  # restore from nuget.org only
```

## Protocol references

This project's HID++ reverse-engineering leans heavily on the open-source
prior art listed below. Logitech's `cpg-docs` is mostly empty as of 2026, so
Solaar remains the de-facto spec.

- [Solaar](https://github.com/pwr-Solaar/Solaar) — Linux receiver manager.
  Canonical reference for HID++ 1.0 sub-IDs (`notifications.py`,
  `common.py`), 2.0 feature definitions (`hidpp20.py`,
  `hidpp20_constants.py`), and Bolt-specific pairing registers
  (`receiver.py:484-531`). `special_keys.py:232-234` defines the Easy-Switch
  CIDs we divert.
- [CleverSwitch](https://github.com/MikalaiBarysevich/CleverSwitch) — Python
  clone of Mouse Flow for Bolt/Unifying. Closest prior art to this project.
- [fwupd logitech-hidpp](https://github.com/fwupd/fwupd/tree/main/plugins/logitech-hidpp)
  — authoritative Bolt USB framing.
- [marcelhoffs/input-switcher](https://github.com/marcelhoffs/input-switcher)
  — known-good CHANGE_HOST byte sequences per device model.
- [HID++ 2.0 draft spec](https://lekensteyn.nl/files/logitech/logitech_hidpp_2.0_specification_draft_2012-06-04.pdf)
  — frame format and `0x1B00/0x1B04 ControlIDBroadcastEvent` layout.

## License

MIT (see `LICENSE`).
