# Alert conditions

Every condition that warrants user attention, with the matching tray
surface, in-app surface, and OS notification. Use this as the source of
truth when adding a new failure mode — pick a row, identify where it
slots in, and make sure all three columns (tray, in-app, toast) are
consistent with the existing pattern.

## Surfaces

- **Tray icon** — two states only: Neutral (silhouette template) or
  Alert (full amber bolt). The transition rule lives in
  `TrayIconStatusController.ResolveState` and reads
  `_permissionStatus != AllGood`, which is set from
  `AppHealthService.Health.IsAlerting`. Any of the three trackers
  (Permissions, Network, Receiver) alerting flips the tray to amber.
- **Tray menu** — gains a `⚠ Fix permissions…` row when permissions are
  denied. No other conditions add menu items.
- **Settings → Status tab** — cards + transport rows render the
  per-condition copy + (where applicable) action buttons. The
  No-receiver and Network-blocked error cards use the design-token
  amber treatment (`CautionTintBrush` background +
  `CautionBrush` icon).
- **OS notification** — three possible titles, one-shot per transition
  into the bad state, re-fires only after the category clears and
  re-enters bad. All come from `App.axaml.cs:378` via
  `LocalNotifications.TryPost` → `INotificationService.Deliver` →
  platform impl.

## Transport states

`TransportHealth.State` is one of: `Starting`, `Healthy`, `Blocked`,
`PermissionDenied`, `Offline`, `Disabled`. Detail copy is kept short
because it's user-facing; heuristic counters (echo rates, warmup
percentages) go to `_logger.LogDebug` instead.

| State | Label | Color | Detail shape |
|---|---|---|---|
| `Starting` | `Starting` | grey | `Starting service` (one line) |
| `Healthy` | `Healthy` | green | label only — no detail |
| `Blocked` | `Blocked` | red | one short remediation hint |
| `PermissionDenied` | `Permission denied` | amber | names the missing permission; UI surfaces a Grant button |
| `Offline` | `Offline` | amber | `No network connection` |
| `Disabled` | `Disabled` | grey | label only |

## Condition table

| # | Condition | Detector | In-app surface (Settings → Status) | Tray icon | Tray menu | OS notification |
|---|---|---|---|---|---|---|
| 1 | Local Network permission denied | `NetworkPermission.Check` (TCC + Firewall on Mac, `INetworkListManager` on Win); polled 1Hz by `PermissionsService` | Permission row + transport rows: `Open Privacy Settings` / `Open Network Settings` button | **Amber** | `⚠ Fix permissions…` | `BoltMate · Permissions issue` / `Local Network permission denied` (or combined w/ HID) |
| 2 | Input Monitoring permission denied (Mac only) | `IOHIDCheckAccess(kIOHIDRequestTypeListenEvent)` | Permission row + Open Privacy Settings button | **Amber** | `⚠ Fix permissions…` | `BoltMate · Permissions issue` / `Input Monitoring permission denied`, or `Local Network + Input Monitoring permissions denied` |
| 3 | Notifications permission denied | `INotificationService.GetAuthorizationStatus()` | Notifications card pill (`Turned off`) + copy | Neutral | — | **Suppressed** — the notification channel is itself the broken surface |
| 4 | Autostart off | `AppAutostart.IsLoaded()` | Launch on startup card toggle | Neutral | — | **Suppressed** — user preference |
| 5 | UDP transport — Permission denied | `UdpTopologyService` gate | UDP row: `Permission denied — Local Network permission required` + button | **Amber** (via Permissions tracker) | `⚠ Fix permissions…` | Suppressed — rolls into Permissions toast (same root cause) |
| 6 | UDP transport — Offline | NIC watcher | UDP row: `Offline — No network connection` | **Amber** | — | `BoltMate · Network issue` / `No network interface available — connect to a network to enable cross-machine sync` |
| 7 | UDP transport — Blocked | echo rate < 40% | UDP row: `Blocked — Multicast traffic is being dropped — check your firewall.` | Amber only if Sync also Blocked | — | None on its own |
| 8 | UDP transport — Starting | warmup | UDP row: `Starting` | Neutral | — | None |
| 9 | UDP transport — Disabled | user toggled sync off | UDP row: `Disabled` | Neutral | — | None |
| 10 | mDNS+TCP sync — Permission denied | gate closed | Sync row: `Permission denied — Local Network permission required` + button | **Amber** (via Permissions tracker) | `⚠ Fix permissions…` | Suppressed |
| 11 | mDNS+TCP sync — Offline | NIC gate | Sync row: `Offline — No network connection` | **Amber** | — | `BoltMate · Network issue` / `No network interface available …` |
| 12 | mDNS+TCP sync — Blocked (mDNS) | publisher start failure / self-echo freshness > 60s | Sync row: `Blocked — Bonjour traffic is being filtered — check your firewall or network.` | Amber only if UDP also Blocked | — | None on its own |
| 13 | mDNS+TCP sync — Blocked (TCP) | peers discovered but no TCP connect | Sync row: `Blocked — Can't reach peers — check the firewall on the other machine.` | Amber only if UDP also Blocked | — | None on its own |
| 14 | Both transports Blocked sustained | UDP AND sync both Blocked for 30s | Error card `Network messages are blocked` (amber); both rows red | **Amber** | — | `BoltMate · Network issue` / `All network paths blocked — peers can't be reached. Check firewall and network settings.` |
| 15 | No Bolt receiver attached | `ReceiverManager.Receivers.Count == 0` sustained 5s | Error card `No Bolt receiver found` with amber-tinted icon background | **Amber** | — | `BoltMate · Receiver issue` / `no Bolt receiver attached — plug one in to enable host-switch fan-out` |
| 16 | Receiver attached, no device linked | manager has receiver but no live devices | Informational empty card `No devices linked up` | Neutral | — | None |
| 17 | Paired but offline device (deep-sleep) | `DeviceEnricher.TryWakeSlotAsync` fails | Log only — device omitted from local list | Neutral | — | None |
| 18 | Update check | `UpdateService.CheckAsync` (stub) | Updates card line | Neutral | — | None |
| 19 | Startup crash | uncaught exception in `OnFrameworkInitializationCompleted` | `boltmate-crash-*.log` only | n/a | — | None |

## Notification dedup rules

- `PermissionDenied` on a transport → handled by the **Permissions**
  toast only. Network toast is suppressed
  (`AppHealthService.UpdateNetworkRaw` lines 145-152). Tray amber is
  owed by the Permissions tracker — no functional gap.
- `Offline` on either transport → fires the **Network** toast with the
  Offline body, skipping the 30s debounce.
- Both transports `Blocked` sustained 30s → fires the **Network** toast
  with the bothBlocked body.
- A single transport `Blocked` (UDP alone OR sync alone) → no toast (at
  least one path still reaches peers); tray stays neutral.
- Each category re-fires only after it resolves and re-enters bad
  (`notified` HashSet in `App.axaml.cs:371-388`).

## Where the wiring lives

- `BoltMate.Core/Topology/TransportHealth.cs` — `TransportState` enum +
  factories.
- `BoltMate.Core/Services/UdpTopology/UdpTopologyService.cs` — UDP
  health emission sites.
- `BoltMate.Core/Services/MdnsTcp/MdnsTcpChannel.cs` — mDNS, TCP, and
  combined Sync emission sites + priority combiner.
- `BoltMate.App/Services/Health/AppHealthService.cs` — three-tracker
  alert engine (Permissions / Network / Receiver) with per-category
  debounce; exposes `AppHealthSnapshot` via `Health` observable.
- `BoltMate.App/App.axaml.cs:355-389` — subscribes the snapshot,
  pushes to tray + posts toasts.
- `BoltMate.App/UI/Tray/TrayIconStatusController.cs` — tray state
  derivation + tooltip composition; `SetHealth(snapshot)` is the
  push-in for cause naming.
- `BoltMate.App/UI/Settings/SettingsViewModel.cs` — `LabelAndColor` for
  the state pills, `ComposeDetail` for the row detail copy, and
  `UdpShowGrantNetworkButton` / `SyncShowGrantNetworkButton` for the
  per-row Grant CTA.
- `BoltMate.App/UI/Settings/SettingsWindow.axaml` — Status tab cards +
  transport rows.
