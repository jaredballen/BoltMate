# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

LogiPlusSwitcher is a **companion** to Logi Options+ — not a replacement, not a competitor. It detects host-switch events from any source on a shared Logitech Bolt receiver and fans the switch out to all other paired devices, so the whole peripheral set follows the user between hosts together.

**Trigger sources, all unified into one fan-out**:

| Source | Detection mechanism |
|---|---|
| Easy-Switch button on keyboard | HID++ feature `0x1B04` (REPROG_CONTROLS_V4) — divert CIDs `0x00D1/D2/D3` and listen for `divertedButtonsEvent` (target host arrives in payload before disconnect) |
| Easy-Switch button on mouse / headset / other Logi+ device | Same as above; CIDs vary slightly on cycle-button devices (see task #14) |
| Mouse Flow (cursor crosses screen edge) | **Snoop Logi+'s own `0x1814 SetCurrentHost` writes** on the management interface, filtered by `swid != ours`. The "echo" the docs warn about IS our Flow detection signal. |
| In-app hotkey | We initiate directly, no detection needed |

**Why this exists**: Logi Flow only triggers multi-device follow when the mouse crosses a screen edge. If the user taps Easy-Switch on the keyboard, the mouse doesn't follow. Flow's edge-detect is also flaky in practice. This app closes the gap.

**Coexistence with Logi Options+ is mandatory** — design contract, not a nice-to-have. Anything that breaks Logi+ is out of scope.

## Stack and platform decisions (locked)

- **.NET 9**, nullable enabled, implicit usings
- **UI**: Avalonia 11 (tray-first, full window optional) — phase 2
- **HID lib**: HidApi.Net — non-exclusive open on macOS via `hid_darwin_set_open_exclusive(0)`; this is what makes Logi+ coexistence work
- **Platforms**: Mac + Windows day one, Linux best-effort (Logi Options+ doesn't exist on Linux; Solaar is the analog there and uses `/dev/hidraw` exclusively)
- **Distribution**: sideload only — notarized PKG/DMG on Mac (Apple Dev ID), MSI/exe on Windows. **Not pursuing Mac App Store** (sandbox + USB HID + background agent combo is reliably rejected; Logi Options+ itself isn't in MAS). Microsoft Store possible later via WinRT HID backend but not v1.
- **v1 scope**: headless service / CLI first, Avalonia tray UI in phase 2

## Solution layout (target)

Current repo has a single console project; phase 1 task #1 splits it:

```
LogiPlusSwitcher/
├── LogiPlusSwitcher.Core/        # HID transport, HID++ protocol, device model — no UI deps
│   └── ILogiHidTransport          # abstraction; libhidapi impl now, WinRT impl later if needed
├── LogiPlusSwitcher.Cli/         # diagnostic CLI / headless service (current Program.cs lives here)
├── LogiPlusSwitcher.App/         # Avalonia tray app (phase 2)
└── LogiPlusSwitcher.Tests/       # protocol tests against captured frames
```

## HID++ protocol cheat sheet

Bolt receiver: VID `0x046D`, PID `0xC548`. Management interface = UsagePage `0xFF00`, Usage `0x0001`. Multiple HID interfaces enumerate; only that one carries HID++ traffic.

**Report IDs**:
- `0x10` — short HID++ (7 bytes total)
- `0x11` — long HID++ (20 bytes total)
- `0x20` — DJ (HID++ 1.0 legacy notifications)

**Frame layout** (HID++ 2.0): `report_id, device_index, feature_index, function|sw_id, params...`
- `device_index`: `0xFF` = receiver, `1..6` = paired device slot
- `function|sw_id`: high nibble = function, low nibble = sw_id (software-assigned, used to correlate request/reply)

**Pick a unique sw_id for this app** (e.g. `0x0E`) and filter incoming events by `swid != ours` to distinguish:
- Logi Options+ writes (these are our Flow detection signal — keep them)
- Our own writes echoing back (filter these out)

**Receiver enable sequence** (must send on attach or notifications are suppressed):
- `10 FF 80 00 00 09 00` — enable HID++ notifications
- `10 FF 80 02 02 00 00` — enumerate paired devices

**Key sub-IDs (HID++ 1.0 / DJ)**:
- `0x40` CONNECT_DISCONNECT — slot unpaired (address `0x02`)
- `0x41` DJ_PAIRING — link state change. `data[0] & 0x40` set = link lost, clear = link up. `address == 0x10` = Bolt encrypted link. `data[1..2]` LE = WPID.

**Key features (HID++ 2.0)** — feature indices are device-specific, **always resolve via IRoot `0x0001` getFeature, never hardcode**:
- `0x0001` IRoot — get feature index by feature ID
- `0x1B04` REPROG_CONTROLS_V4 — Easy-Switch CIDs `0x00D1` (host 1), `0x00D2` (host 2), `0x00D3` (host 3). `getCidInfo` flags: `& 0x20` divertable, `& 0x40` persistently divertable. `setCidReporting` bfield `0x03` = divert valid + divert set. `divertedButtonsEvent` fires on press BEFORE the device-internal host switch executes (~50ms window).
- `0x1814` CHANGE_HOST — `read fn=0x00` returns `(numHosts, currentHost)`; `write fn=0x10` SetCurrentHost is fire-and-forget (no reply). **No event is emitted on host change.**
- `0x1815` HOSTS_INFO — read-only poll for host names / capabilities

**Bolt-specific pairing registers** (for slot metadata — names, WPID, etc.):
- `BOLT_UNIQUE_ID`, `BOLT_DEVICE_NAME`, `BOLT_PAIRING_INFORMATION`, `BOLT_DEVICE_DISCOVERY`, `BOLT_PAIRING` — see Solaar `receiver.py:484-531`.

## Reference projects (the spec, since Logitech's `cpg-docs` is incomplete)

- **Solaar** (`github.com/pwr-Solaar/Solaar`) — authoritative Linux receiver manager, supports Bolt. Key files:
  - `lib/logitech_receiver/notifications.py:137-220` — 0x41/0x42 parsing
  - `lib/logitech_receiver/common.py:713-720` — sub-ID enum
  - `lib/logitech_receiver/receiver.py:484-531` — BoltReceiver, Bolt registers
  - `lib/logitech_receiver/hidpp20.py:2064-2088` — `get_host_names`
  - `lib/logitech_receiver/settings_templates.py:1295-1318` — `ChangeHost` (proves write-only)
  - `lib/logitech_receiver/special_keys.py:232-234` — Host_Switch_Channel CIDs
- **CleverSwitch** (`github.com/MikalaiBarysevich/CleverSwitch`) — Python clone of Flow for Bolt/Unifying. Most directly comparable prior art. Architecture maps to our C# split cleanly.
- **fwupd Bolt runtime** (`plugins/logitech-hidpp/fu-logitech-hidpp-runtime-bolt.c`) — authoritative Bolt USB framing reference.
- **marcelhoffs/input-switcher** — write-only side, useful for known-good CHANGE_HOST byte sequences per device.
- **Logitech `cpg-docs`** — official docs repo but mostly empty as of 2026; Solaar remains the de-facto spec.

Drafts of the HID++ 2.0 spec (Lekensteyn's mirror) cover frame format and 0x1B00/0x1B04 ControlIDBroadcastEvent layout.

## Platform gotchas

- **macOS**: Input Monitoring permission required for HID reads. `dotnet run` from a terminal inherits the terminal app's grant. If reports come back empty or device open fails, that's the first check.
- **macOS Logi+ coexistence**: HidSharp opens IOKit devices in a way that conflicts with Logi+. HidApi.Net's libhidapi binding supports `hid_darwin_set_open_exclusive(0)` for shared access. Use it.
- **Windows**: Standard HID API, no special permission. Logi+ coexists fine because Windows HID is shared by default.
- **Linux**: `/dev/hidraw*` requires udev rule for non-root access. Don't run alongside Solaar — both hold the hidraw node.

## Build / run

From `LogiPlusSwitcher/` (the solution directory):

```
dotnet restore
dotnet build
dotnet run --project LogiPlusSwitcher/LogiPlusSwitcher.csproj
```

After the phase-1 restructure (task #1), the run target becomes the `Cli` project.

## Working with this project

15 phase-1 tasks tracked in TaskList, dependency-ordered. Task list is the working backlog; do not free-form invent steps without checking it. Phase 2 (Avalonia UI, settings, onboarding, multi-receiver UX) is deliberately deferred until the headless service detects + fans out reliably.
