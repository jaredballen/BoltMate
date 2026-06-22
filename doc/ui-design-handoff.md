# BoltMate UI Design Handoff

## What BoltMate is

BoltMate is a small tray utility that fixes a gap in how Logitech keyboards, mice, and headsets behave when one person uses several computers. Logitech sells a small USB receiver called the "Bolt receiver" that lets one keyboard or mouse talk to up to three computers at once; the user taps a button on the device (Easy-Switch) to jump between hosts. Logitech's companion app, **Logi Options+**, also offers "Flow," which makes the mouse follow your screen edge between computers. Both features only move **one device at a time**. Tap Easy-Switch on the keyboard and the mouse stays behind on the previous computer; let Flow drag the mouse over and the keyboard stays behind.

BoltMate is the missing fan-out. It watches the Bolt receiver for a host-switch event from **any** source — a button press, a Flow edge crossing, or a sibling event arriving from another computer on the LAN — and pushes the matching switch out to **every other paired device on every paired machine** so the whole peripheral set follows the user together. It is a **companion**, not a replacement: Logi Options+ keeps handling pairing, renaming, friendly-name editing, and per-host hotkeys. BoltMate handles only the cross-device + cross-machine fan-out that Flow doesn't.

It lives in the system tray (menu bar on Mac, system tray on Windows). There is no main window. The user's interaction model is: install it, run a one-time welcome wizard that grants two OS permissions, then forget about it. They will open the settings window only when they want to confirm things are working, check that their other machines can see each other, or fix a permissions warning the tray icon is showing.

## User & context

**Who.** Power users who own multiple Mac and Windows machines and at least one set of high-end Logitech peripherals (MX Master 3S, MX Keys, etc.) bound to a shared Bolt receiver. They already use Logi Options+ and have already configured pairings/host slots there. They are technical enough to understand "this lives in the tray," "this needs Local Network access," "this is sideloaded software."

**When.** Almost never, after setup. The expected interaction cadence is:

- **Once at install**: welcome wizard, grant Local Network (Mac + Win) and Input Monitoring (Mac only), tick "Launch at login," done.
- **Occasionally**: glance at the tray icon. A green check, red X, or yellow `!` badge tells them the state without clicking.
- **Rarely**: open Settings → Status to confirm devices and peers are healthy, or click "Fix permissions…" if the tray went yellow.
- **Even more rarely**: About tab to check for updates or open the logs folder.

**What they're trying to accomplish.** Move between their computers without thinking about it. The success state is invisibility — they tap Easy-Switch and everything follows. The UI exists to confirm "yes, it's working" and to recover from the permission-denied case.

## Platform constraints

- **Avalonia 12 on .NET 9/10**, single C# codebase, native shell on Mac + Windows. Linux is best-effort.
- **Tray-first**, no main window. `ShutdownMode = OnExplicitShutdown`. Closing the settings window hides it (keeps the tray icon alive) — it does not exit the app.
- **macOS**: app is `LSUIElement` (menu-bar only, no Dock icon by default). When the Welcome or Settings window opens, code flips the macOS `NSApplicationActivationPolicy` to Regular so a Dock icon appears for the duration; closing the window flips back to Accessory so the Dock icon disappears again. The app menu is rewritten from "Avalonia Application" to "BoltMate" at startup.
- **Windows**: standard tray icon. Left-click on the tray icon opens Settings → Status directly (Win convention). Right-click shows the menu. On Mac the click opens the menu (Mac convention) — left-click is not bound.
- **Cross-platform parity is mandatory.** Every visual choice must work on both Mac and Win and feel native-ish on each. Currently the app uses the same `FluentAvaloniaTheme` on both platforms (light + dark follow the OS).
- **Single-instance lock.** A lock file in the settings dir prevents a second copy from spawning, e.g. after autostart kicks in mid-welcome.
- **Sideload only.** Notarized PKG/DMG on Mac, MSI/exe on Windows. Not in any app store.

## Information architecture

```
Tray icon (menubar / system tray)
└── Context menu
    ├── [⚠ Fix permissions…]   ← only when any permission denied
    ├── ─────────              ← separator above only when alerting
    ├── Status                  → opens Settings, Status tab
    ├── About                   → opens Settings, About tab
    ├── License                 → opens Settings, License tab
    ├── ─────────
    └── Quit                    → exits app

First-run / Fix-permissions Welcome window  (modal-ish; one window, page swap)
├── PageWelcome              first-run only — icon, title, autostart toggle, "Get started"
├── PageNetworkPrimer        primer for Local Network permission
├── PageNetworkRefusal       shown after Not now or denied grant
├── PageInputMonitoringPrimer   (Mac only) primer for HID / Input Monitoring
├── PageInputMonitoringRefusal  (Mac only) shown after Not now or denied
├── PageDone                 "You're all set" → "Open BoltMate"
└── PageLinux                short-circuit: Linux has no prompts; "Open BoltMate"

Settings window (lazy-constructed, hidden-not-closed across opens)
├── Side nav (FluentAvalonia NavigationView, left-aligned, 200px pane)
│   ├── Status   (Globe icon)
│   ├── About    (Important icon)
│   └── License  (Document icon)
└── Content panes (three stacked Grids, only one IsVisible at a time)
    ├── Status pane
    │   ├── Card: "This machine"        — list of locally online devices
    │   ├── Card: "Network access"      — local-network permission state + "Open Privacy Settings" button
    │   └── Card: "Peers"               — list of LAN peers + their online devices
    ├── About pane
    │   ├── Header: "BoltMate" + version line
    │   ├── Card: "Updates"     — last-checked timestamp + "Check for updates" button + status line
    │   ├── Card: "Startup"     — "Launch BoltMate at login" checkbox + detail line
    │   ├── Card: "Privacy"     — "Send anonymous diagnostics" checkbox + explainer
    │   └── Card: "Diagnostics" — "Open logs folder" button + log path line
    └── License pane             — placeholder ("Licensing — coming soon"), disabled key field + Activate button

Native OS surfaces
├── Local notification: "BoltMate needs permissions" / "Click to fix"
│   (macOS NSUserNotification; tray-badge-only fallback on Win/Linux)
└── Tray icon badge (rendered over base silhouette)
    ├── Neutral   — silhouette only
    ├── Good      — green check (peer reachable within 15s)
    ├── Bad       — red X (peers known but none seen within 15s)
    └── Alert     — yellow ! (any OS permission denied) — overrides Good/Bad
```

**NOTE re CLAUDE.md.** CLAUDE.md describes the tray as having "per-receiver sub-sections with per-slot 'Switch to Host N' submenus" and Settings tabs of "General, Receivers, Topology, Network, Status, Updates." The code in `TrayMenuController.cs` and `SettingsWindow.axaml` has been simplified down to the structure above. Treat the inventory in this doc as the source of truth.

## Screen-by-screen inventory

### Tray menu

**File**: `src/BoltMate.App/TrayMenuController.cs` (built in code; XAML shell in `App.axaml`).

**Purpose**: The app's only persistent surface. Five static items plus an optional alert item, no per-receiver dynamism.

| Item | Visibility | Action |
|---|---|---|
| `⚠ Fix permissions…` | Only when any OS permission denied | Opens Welcome window at the first ungranted primer page |
| `─` separator | Only when alert item visible | — |
| `Status` | Always | Opens Settings → Status |
| `About` | Always | Opens Settings → About |
| `License` | Always | Opens Settings → License |
| `─` separator | Always | — |
| `Quit` | Always | `desktop.Shutdown()` — exits the app |

**State driver**: `PermissionsService` polls Network + Input Monitoring every 2s. When the overall roll-up (`OverallStatus.AllGood` vs `AnyDenied`) flips, the menu is rebuilt to add/remove the "Fix permissions…" item.

**Platform**: Mac — click the tray icon to open the menu (native convention). Windows — left-click opens Settings → Status; right-click opens this menu.

### Welcome flow

**Files**: `src/BoltMate.App/Welcome/WelcomeWindow.axaml(.cs)`.

A single 560×460 non-resizable window with seven page Grids stacked; one is visible at a time. State machine lives in code-behind. The user is forced through this on first run — closing the window with `[x]` / `Cmd-W` quits the app entirely. Every page has the same shape: large icon → big title → paragraph → optional status line → action buttons pinned bottom-right.

The window is also reused for the on-demand "Fix permissions…" entry point on subsequent launches (skips the welcome page, jumps straight to the first ungranted primer, dismissal does not quit).

#### Step 1 — `PageWelcome` (first run only)

- 96×96 BoltMate app icon (centered, top)
- Title: "Welcome to BoltMate"
- Body: "BoltMate keeps your Logitech Bolt peripherals together as you switch between computers."
- Checkbox (centered above buttons): "Launch BoltMate when I sign in" (default ON)
- Button (bottom right): **Get started** (accent style)

`Get started` applies the autostart toggle, saves a `WelcomeStepCompleted` checkpoint, and advances to the next page that needs work.

#### Step 2 — `PageNetworkPrimer` (Mac + Windows)

- 56pt Globe symbol icon, centered
- Title: "BoltMate needs Local Network access"
- Body: "BoltMate sends small heartbeats on your LAN so peripherals follow you between your computers. Nothing leaves your network — no traffic crosses the internet."
- Status line (tertiary text): updated live — "Local Network access: granted" / "denied"
- Buttons: **Not now** / **Grant** (accent)

`Grant` calls `NetworkPermission.Request()`. On macOS this foregrounds the app and fires a UDP multicast packet to trigger the TCC dialog. On Windows it tries to remove any pre-existing Block firewall rule, then binds a TcpListener for 5s to surface the Windows Defender prompt; falls back to opening the Firewall control panel if it can't.

A page-scoped watcher subscribes to `IPermission.IsGrantedChanged` and auto-advances when the user grants in System Settings without re-clicking.

#### Step 3 — `PageNetworkRefusal` (after Not now or denied grant)

- 56pt Important symbol icon in caution color, centered
- Title: "Local Network is required"
- Body: "BoltMate can't sync between your computers without LAN access. Without it, you'd have to manually press Easy-Switch on each device when moving between machines."
- Status line: same live update
- Buttons: **Quit BoltMate** / **Grant** (accent)

`Quit BoltMate` calls `desktop.Shutdown(1)`. No third option — the user must grant or quit.

#### Step 4 — `PageInputMonitoringPrimer` (Mac only)

- 56pt Keyboard symbol icon, centered
- Title: "BoltMate needs HID device access"
- Body: "macOS labels this 'Input Monitoring' because it gates all HID device access. BoltMate uses it ONLY to read host-switch events from your Bolt receiver. BoltMate does not log keystrokes or any other input."
- Status line + Not now / **Grant** buttons (same shape as Step 2)

`Grant` calls `IOHIDRequestAccess`. macOS may force a relaunch after this grant; the wizard's checkpoint flags let it resume on the next page.

#### Step 5 — `PageInputMonitoringRefusal` (Mac only)

Same shape as Step 3, with an expanded body explaining BoltMate can't function at all without HID access. Buttons: **Quit BoltMate** / **Grant**.

#### Step 6 — `PageDone`

- 64pt green Accept (checkmark) symbol icon, centered
- Title: "You're all set."
- Body: "BoltMate is now active in your menu bar."
- Button: **Open BoltMate** (accent)

`Open BoltMate` flips `HasShownWelcome` to true, fires `WelcomeCompleted`, closes the window. The App layer then runs the rest of bootstrap and opens Settings to the Status tab.

#### Step 7 — `PageLinux` (Linux fast-path)

- 96×96 app icon centered
- Title: "Nothing to do — opening BoltMate"
- Body: "BoltMate doesn't need any permission prompts on Linux."
- Button: **Open BoltMate** (accent)

### Settings — Status

**File**: `src/BoltMate.App/SettingsWindow.axaml` (StatusPage Grid).

**Window chrome**: 820×560 (min 680×460), centered, FluentAvalonia NavigationView left rail (200px wide), header "BoltMate Settings."

**Pane header**: "Status" (22pt SemiBold) + dim subtitle: "Online devices on this machine, the local network access state, and any peers reachable on the LAN."

**Three stacked cards** in a scrolling column (CardStrokeColorDefaultBrush border, CardBackgroundFillColorDefaultBrush fill, 6px corner radius, 14px padding):

1. **This machine** — list of currently linked-up devices on locally attached Bolt receivers. Each row is a small SubtleFillColorSecondaryBrush pill with two lines:
   - Line 1 (SemiBold 13pt): `{DisplayName} · wpid 0x{WPID:X4}` (e.g. "MX Keys · wpid 0xB35F")
   - Line 2 (12pt, monospace, secondary): `current H{N} → {receiverName}   battery {pct}%{ (charging)}`
   - Empty state: "No devices currently linked up."

2. **Network access** — current local-network permission state from `NetworkPermission.Check()`:
   - Detail text: e.g. "Local Network access: granted" / "denied" / "Network: Private (allowed by default)" / "Network: Public + firewall blocks"
   - Button: "Open Privacy Settings" (Mac) / "Open Network Settings" (Win), only visible when Denied

3. **Peers** — discovered LAN peers from the UDP topology service:
   - State line: "N peer(s) reachable." or "No peers discovered yet."
   - Per-peer pill (max 2 shown, overflow text "+ N more peer(s) not displayed"):
     - Header row: `{hostname}  [online|silent]` left, `last seen {Nms|Ns|Nm} ago` right
     - Meta line (monospace): `machine {first 8 hex chars of MachineId}   recv {UniqueReceived count}`
     - Indented per-device list (monospace, with `●` bullet): `slot {N} · wpid 0x{hex} · {name}`
     - Or fallback: `(no devices online on this peer)`

**Refresh cadence**: 1Hz `DispatcherTimer` re-runs all three sections.

### Settings — About

**Pane header**: "BoltMate" (22pt SemiBold) + dim version line ("Version 0.5.123-abc").

**Four cards** in a scrolling column:

1. **Updates**
   - "Last checked:" label + value ("never" or a local time stamp)
   - Button: "Check for updates"
   - Status line (updated after click): "Checking…" → "You're up to date on X" / "Update available: V. Download: URL" / "Update check failed: …"
   - Implementation note: `UpdateService.CheckAsync` is a stub; only stamps `LastUpdateCheckUtc`.

2. **Startup**
   - Checkbox: "Launch BoltMate at login" — wired to `IPermission.Autostart`, which on Mac installs/uninstalls a LaunchAgent plist, on Windows registers a Task Scheduler entry
   - Detail line: "Registered. BoltMate will start automatically when you log in." / "Off. Launch manually from Applications / Start Menu." / Disabled: "Disabled: run from a published build (not 'dotnet run') to enable launch-at-login."
   - Live-bound: System Settings toggle of Login Items propagates back into the checkbox via the polling service.

3. **Privacy**
   - Checkbox: "Send anonymous diagnostics" (default OFF; persisted to `AppSettings.TelemetryEnabled`)
   - Body: "Off by default. When on, error reports and de-identified usage counts go to Azure App Insights. No device serials, key strokes, or file paths are ever sent."

4. **Diagnostics**
   - Button: "Open logs folder" — reveals `AppPaths.LogsDirectory` in Finder / Explorer / xdg-open
   - Path line: "Logs: /path/to/logs"

### Settings — License

**Pane header**: "License" (22pt SemiBold) + dim "Licensing — coming soon"

**One placeholder card**:
- "Activation key" label (SemiBold)
- Disabled TextBox
- Disabled "Activate" button
- Body: "Licensing is not yet available. The current build runs all features without an activation key."

### Dialogs / popups / notifications

The app is intentionally minimal here. The only non-window UI surfaces:

- **Local notification** — `LocalNotifications.TryPost("BoltMate needs permissions", "Click to fix")` once per app session when permissions go denied. macOS uses `NSUserNotification` via Obj-C P/Invoke. Windows and Linux: no notification; tray badge + "Fix permissions…" menu item carry the signal. Notification is delivered at most once per process lifetime; no persistent state across launches.
- **Tray icon badge** — see "Information architecture" diagram above. Composited at runtime by `TrayIconStatusController` over the base silhouette in lower-right corner (~40% of icon dimension). Glyphs: ✓ for Good (LimeGreen), × for Bad (OrangeRed), `!` for Alert (Gold). Template-icon flag is dropped on Mac when a badge is present (otherwise AppKit would template-invert the colored sticker).
- **OS-native prompts surfaced by the wizard's Grant buttons** — macOS Local Network TCC dialog, macOS Input Monitoring System Settings pane jump, Windows Defender Firewall "Allow access" prompt. These are OS UI, not BoltMate UI.

There are no in-app modal dialogs, message boxes, confirm prompts, or toasts beyond the above.

## Data model the UI exposes

A designer should know these terms — they appear in labels and would appear in any new screen:

- **Bolt receiver** (or just "receiver"): the small USB dongle. Each one is identified by a `Serial` and has a `BLE address` / "host identifier." May have up to 6 paired devices.
- **Paired device**: a keyboard, mouse, headset, etc. bound to a slot 1–6 on a specific receiver. Has a `DisplayName` (e.g. "MX Master 3S"), `Wpid` (wireless product id, 16-bit hex), an optional user-editable `FriendlyName`, a `LinkUp` boolean, an optional `LastKnownBattery` (percent + charging flag), and an optional `LastKnownCurrentHost` (which of the device's host slots is active now).
- **Host slot / Host N**: each Bolt-capable device can remember up to 3 host computers (the buttons labeled 1/2/3 on the device). Numbered 0..2 internally, displayed as H1/H2/H3.
- **Host binding**: a row per host slot on a device, telling us which BLE address (= which receiver = which computer) that slot points to, plus the host's friendly `ReceiverName` (Logi Options+ sets this to the OS hostname).
- **Peer / machine**: another computer on the LAN that is also running BoltMate. Identified by a stable `MachineId` (UUID) plus a `Hostname`. Carries a list of the receivers + currently-online devices it sees.
- **Fan-out**: the act of catching a host-switch on one device and pushing the matching switch to siblings. The user shouldn't have to know this word — but the UI surfaces *evidence* of it (devices following each other, peers exchanging announcements).
- **Topology**: the cross-machine sync layer. Off/on master switch lives in `AppSettings.Topology.Enabled` (currently force-enabled at startup; no UI to toggle).
- **Permissions**: Network (Local Network access on Mac, firewall rule on Win), Input Monitoring (Mac only — HID device access), Autostart (Login Items / Task Scheduler).

## Current visual state

The app uses **FluentAvalonia** as the primary theme on both Mac and Windows, with Avalonia's stock FluentTheme as a fallback. `PreferSystemTheme="True"` means light/dark follows the OS. `PreferUserAccentColor="True"` picks up the user's system accent.

**No custom theme files, brushes, resource dictionaries, or styles** exist in the App project — every visual decision is either FluentAvalonia's default or a one-off inline value in `SettingsWindow.axaml` / `WelcomeWindow.axaml`. Examples of inline values currently in the code:
- Hex foregrounds: `#9AA0A6` (caption gray), `#B5BAC2` (slightly lighter), used directly on `TextBlock.Foreground`
- Border / fill brushes: `CardStrokeColorDefaultBrush`, `CardBackgroundFillColorDefaultBrush`, `SubtleFillColorSecondaryBrush`, `SolidBackgroundFillColorBaseBrush`, `TextFillColorSecondaryBrush`, `TextFillColorTertiaryBrush`, `SystemFillColorCautionBrush` — all standard FluentAvalonia dynamic resource keys
- Monospace stack: `JetBrains Mono,Menlo,Consolas,monospace` used for device/peer detail lines
- Font sizes: 11 / 12 / 13 / 14 / 22 / 24 in various spots — no scale tokens

**Window chrome** is Avalonia's default per OS. No Mica/blur/translucency on Win 11 (Mica intentionally not enabled). No custom title bar.

**Assets** in `src/BoltMate.App/Assets/`:
- `app-icon-1024.png` — 1024×1024 app icon used as the splash image on the Welcome and Linux pages
- `app-icon.icns` — macOS icon bundle
- `app-icon.ico` — Windows icon bundle
- `app-icon.svg` — vector source
- `tray-icon-light.png` + `tray-icon-light@2x.png` — light-theme tray silhouette + retina
- `tray-icon-dark.png` + `tray-icon-dark@2x.png` — dark-theme tray silhouette + retina

Additional brand assets in `res/` at the repo root (not bundled): `BoltMate_logo.svg`, `BoltMate_wordmark.svg`, `BoltMate_wordmark_wide.svg`.

**FluentAvalonia symbols** used as inline icons: `Globe`, `Important`, `Document`, `Keyboard`, `Accept`. These are font-glyph icons sized at 14pt (nav) or 56–64pt (welcome pages).

The current look is honest-to-FluentAvalonia: card-and-list Win11-style with no brand identity layered on top. No custom illustrations, no color besides the OS accent and the alert/success/error semantic brushes. There is no logo or wordmark anywhere in the running UI except the app icon image on two welcome pages.

## Known rough edges

- **Welcome flow is the explicit ask for rework.** Observations the designer should know:
  - The Welcome page's "Launch at login" checkbox is awkwardly placed (centered, above the buttons rather than near them) and applies silently with no confirmation.
  - The primer/refusal page pairs duplicate copy — Refusal just rewords the Primer in a slightly scarier tone with the same icon language but a caution-colored icon. They could be one page that escalates in place.
  - Page transitions are an `IsVisible` swap with no animation, no progress indicator, no "Step 2 of 3" affordance. Users don't know how many pages are coming.
  - The "Done" page is bare — single sentence and an Open button. Could double as a quick-tour or "what to look at next" affordance.
  - The window is non-resizable at 560×460 and the centered icon + heading + paragraph leaves a lot of empty vertical space on the Welcome and Done pages while feeling cramped on the longer-copy Refusal pages.
  - The wizard force-quits the app on close on first run — there's no soft-exit / "I'll do this later" path.
- **Settings is a 3-tab Win11 NavigationView stencil with no BoltMate identity.** The page headers are plain text; cards stack vertically; there's no visual representation of what the app *does* (no animation, no diagram of devices following the user across machines, no live-when-something-happens indicator).
- **Status tab uses monospace strings for device and peer details** — functional but reads like a debug console rather than a status dashboard.
- **License tab is a placeholder** with disabled controls. May want a friendlier "coming soon" treatment or to be hidden entirely until shipping.
- **CLAUDE.md is stale relative to code.** It mentions tabs (General, Receivers, Topology, Network, Status, Updates) and per-receiver tray submenus that don't exist in the current build. Designer should ignore CLAUDE.md for the IA — this doc is the truth.
- **No empty-state illustrations** anywhere. "No devices currently linked up" / "No peers discovered yet." are plain text.
- **Tray badge glyphs are programmatically drawn shapes** — check/X/exclamation built with `DrawLine`/`DrawEllipse`. Designer may want to provide proper monochrome glyph assets for crisper rendering at retina sizes.
- **Topology toggle is hidden.** `AppSettings.Topology.Enabled` is force-set to `true` at startup with no UI to turn it off. If LAN sync becomes opt-in, a new control will need a home.

## Out of scope for redesign

The following capabilities belong to Logi Options+ and **must not** appear in any new BoltMate screen:

- **Pairing / unpairing** a device with a receiver
- **Renaming** a device (writing its friendly name)
- **Editing per-host friendly names** (the names that appear on each device's host slot label)
- **Hotkey binding** / custom button assignments
- **DPI tuning, scroll behavior, gesture configuration** — anything device-level

Also off-limits:

- A main-window-first redesign. The app's whole purpose is to be invisible until summoned. The tray + on-demand settings model is locked.
- Any feature that requires hard-blocking Logi Options+ from also accessing the receiver. Coexistence is the design contract.
- Custom hardware controls (sliders for cursor speed, etc.) — that's Options+ territory.

In-scope and welcome:

- A more confident visual identity (the brand assets in `res/` exist but aren't wired into any screen).
- A friendlier first-run sequence that explains the product, not just the permissions.
- A Status surface that *shows* the cross-machine sync working — a visual model of devices and peers, not a stack of monospace lines.
- Empty states and error states that feel intentional.
- An About surface that doesn't feel like a settings dump.
