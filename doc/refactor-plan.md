# BoltMate refactor plan

Critical pass over the codebase against modern C# / .NET best practices and
the desired architecture (MVVM via ReactiveUI, DI/IoC via plain
`IServiceCollection`/`IServiceProvider`, strong interface design,
one service per directory).

Originally captured 2026-06-22. Revision adds regression-prevention strategy.

---

## Hard constraint

**Permission handling, HID protocol support, and topology/fan-out logic
MUST NOT regress.** UI regressions are acceptable risk during the
transition; the underlying device-control surface is not.

This shapes everything downstream:

- Phase 0 (characterization tests) lands **before** any refactor PR
- PRs that touch the Core lib (interfaces, DI, ConfigureAwait,
  TimeProvider) carry an explicit manual smoke checklist
- The MVVM PR is sequenced LATE because it's UI-only and therefore the
  lowest-stakes path even though it has the most LOC
- The DI PR is sequenced AFTER interface extraction so the wiring step
  is mechanical, not a redesign

---

## Findings summary

### ✅ Already strong (no work needed)
| Area | State |
|---|---|
| Null comparisons | `is null` / `is not null` everywhere — zero `== null` / `!= null` |
| Sync-over-async | Zero `.Result` / `.Wait()` deadlock patterns |
| File-scoped namespaces | 56 of 57 .cs files |
| Records for DTOs | 25 records vs 53 classes |
| Switch expressions | 16 expressions vs 2 statements |
| `using` blocks vs declarations | Only 2 blocks left (`MdnsTcpChannel.cs`, `TrayIconStatusController.cs`) |
| `async void` | Only 1, documented event-handler continuation |
| Read-only collection surfaces | `IReadOnlyDictionary` / `IReadOnlyList` used appropriately |

### ⚠️ Mechanical fixes
| Area | Sites | Risk |
|---|---|---|
| Enum `==`/`!=` → `is`/`is not` | ~25 | Low — semantically identical |
| `ConfigureAwait(false)` audit in Core | ~50 missing awaits | **Medium for topology** — see Phase 5 |
| `BoltMate.App/Program.cs` → top-level statements | 1 file | None |
| Remaining 2 `using` blocks → declarations | 2 sites | Low — confirm dispose scope unchanged |
| `ArgumentNullException.ThrowIfNull` at public ctors | ~30 ctors | Low — fail-fast on bad callers |
| Primary constructors (C# 12) | ~20 classes | None |

### ❌ New architecture
| Area | State |
|---|---|
| MVVM | Zero — three windows are pure code-behind: `App.axaml.cs` 481 LOC, `SettingsWindow.axaml.cs` 612 LOC, `Welcome/WelcomeWindow.axaml.cs` 564 LOC |
| DI/IoC in main app | Zero — `App.axaml.cs` does 10+ direct `new` constructions; lifecycle by hand via `CompositeDisposable` |
| Service interfaces | 1 of 14 `*Service` types has an interface (`PermissionsService` → `IPermissionsService`); plus ~9 service-shaped types without `*Service` suffix that also lack interfaces |
| Per-service directories | Today: grouped by domain (`Core/HidPp/Features/` holds 8 services flat); ask: one directory per service with co-located interface + class |
| `TimeProvider` injection | 43 raw `DateTime(Offset).UtcNow` reads — time-sensitive code is not deterministically testable |
| `[LoggerMessage]` source-gen | Zero — all logging uses interpolation/format strings; hot paths in HID dispatch and topology emit at every notification |
| `IAsyncDisposable` | Zero — network/HID transports do sync shutdown of async work |
| Fire-and-forget tasks | `_ = Task.Run(...)` silently swallows exceptions (e.g. `SwitcherService` verify-and-retry) |

---

## Service interface inventory

Concrete services with no interface (interface-extraction candidates):

**HID / Core**: `BoltReceiver`, `ReceiverManager`, `BatteryService`,
`ChangeHostService`, `DeviceFriendlyNameService`, `DeviceInfoService`,
`DeviceNameService`, `HostsInfoService`, `ReprogControlsService`,
`RootService`, `HidPpClient`

**Topology / Switching**: `SwitcherService`, `UdpTopologyService`,
`MdnsTcpChannel`, `TopologyCorrelator`

**App layer**: `AppHealthService`, `PermissionStatusService`,
`UpdateService`, `DeviceEnricher`, `TrayMenuController`,
`TrayIconStatusController`, `HostBindingPersistence`

Total: **~22 services** to extract interfaces for.

Tests today mock at the HID **transport** boundary
(`FakeReceiverTransport` / `FakeReceiverConnection`) rather than per
service — that pattern stays. Interface extraction is purely additive
and unlocks per-service mocking AND enables DI.

---

## Critical-path surface (do not regress)

The refactor must preserve these end-to-end behaviors at all times. Each
PR carries the relevant subset as a smoke checklist.

### Permissions (Mac + Win)
- TCC `ListenEvent` (Input Monitoring) read on Mac
- TCC `LocalNetwork` read on Mac (probe-based since `tccutil reset` is
  unreliable on Sequoia)
- Windows Defender Firewall per-exe rule detection
- Welcome wizard fresh-install re-entry (settings.json wiped → wizard
  must reappear, step gates honored)
- `PermissionStatusService` 2 Hz roll-up → tray badge state
  transitions (Healthy / Alert / Bad)
- "Fix permissions…" conditional tray-menu item

### HID protocol (BoltMate.Core + HidApi / IOKit / Win backends)
- HID++ frame parse: report id `0x10` short, `0x11` long, `0x20` DJ
- `IRoot 0x0001` feature-index lookup
- `0x41 DJ_PAIRING` link-up / link-lost → `BoltReceiver.LinkEstablished` /
  `LinkLost` emissions
- `0x1B04` divertedButtons → `BoltReceiver.HostSwitchPresses`
- `0x1815 SetCurrentHost` snoop → `BoltReceiver.FlowHostSwitches`
- `0x1004` battery push → `BoltReceiver.BatteryStatusChanged` +
  `device.LastKnownBattery`
- `0x1D4B WIRELESS_DEVICE_STATUS` → `BoltReceiver.DeviceReady`
- `0x1814 CHANGE_HOST` write via `BoltReceiver.TrySwitchHost` —
  verify-and-retry path in `SwitcherService` fires on no-disconnect-after-1200ms
- `0x1815 HOSTS_INFO` chunked friendly-name reads — retry-on-default,
  upgrade-only merge in `DeviceEnricher`

### Topology / Fan-out
- UDP broadcast + multicast loop (`UdpTopologyService`)
- Self-echo health gate (UDP transport health)
- mDNS publish + browser via Makaretu (`MdnsTcpChannel`)
- TCP listener accept loop (`MdnsTcpChannel`)
- Multi-alias hostname matching (`LocalHostIdentity` — Mac ComputerName +
  LocalHostName + DNS form; Win MachineName + DNS form)
- `TopologyCorrelator.Prune` — drops announcements that don't reference
  any local alias
- `TopologyCorrelator.OnAnnouncement` — `LastSwitchEvent` route +
  `CheckRemoteReappearance` route
- `_pendingLost` reappearance window (10 s)
- `SwitcherService.LocalSwitchTriggers` emission — only for
  non-RemoteTopology sources, to avoid peer-to-peer loop
- `SwitcherService.FanOut` sibling iteration: skip-originator,
  skip-no-binding, skip-offline, skip-no-ChangeHostIndex
- `SwitcherService.RequestTopologyFanOut` — called by `TopologyCorrelator`
  on reappearance, by CLI on user-requested switch

---

## PR sequencing (re-prioritised)

Order = risk-shaped. Each PR has explicit regression guards before merge.

### Phase 0 — Characterization tests (PREREQUISITE)
**Goal**: lock in current behavior with tests **before** touching code.

Today's 112 tests already cover much of `SwitcherService`,
`TopologyCorrelator`, host-name matching, fan-out routing. Gaps:

- [ ] `BoltReceiver.OnNotification` dispatch — table-driven test feeding
      synthetic frames (one per notification type: 0x41 link up/down,
      0x1B04 divert, 0x1815 snoop, 0x1004 battery, 0x1D4B ready)
- [ ] `DeviceEnricher` end-to-end with fake transport: on link-up the
      heavy-read pass runs; on DeviceReady the refresh runs; on
      LinkUp=false the read is skipped
- [ ] `UdpTopologyService` round-trip: emit announcement, receive on
      loopback, verify same `LastSwitchEvent` survives
- [ ] `MdnsTcpChannel`: self-echo classifies as Healthy; absence
      classifies as Blocked
- [ ] `PermissionsService` smoke (per-platform): granted-state on Mac,
      denied-state on Win — at least a happy-path
- [ ] `LocalHostIdentity.Matches`: alias matching unit tests
- [ ] `SwitcherService` verify-and-retry: when CHANGE_HOST is ignored
      (device stays LinkUp), retry fires 1200 ms later, then again

These tests must be green before AND after each subsequent PR.
**No PR merges if any of these break.**

**Manual smoke**: full Mac+Win fan-out cycle (kb easy-switch back and
forth, mouse follows). Capture log + bindings.

---

### PR 1 — Mechanical modernization (low risk)

- Enum `==`/`!=` → `is`/`is not` (case-by-case where readable)
- `BoltMate.App/Program.cs` → top-level statements
- Remaining 2 `using` blocks → declarations (`MdnsTcpChannel`,
  `TrayIconStatusController`) — VERIFY dispose scope unchanged
- `ArgumentNullException.ThrowIfNull` at public service ctors
- Primary constructors where pure assign-to-field (e.g. simple HidPp
  feature services)

**Regression guard**:
- Phase-0 tests must stay green
- Visual diff of `using`-block→declaration sites to confirm dispose
  timing
- Manual smoke: app launches, tray icon visible, fan-out works

**Explicitly DEFERRED to Phase 5**:
- `ConfigureAwait(false)` audit — moved later because Core code paths
  interact with topology timing; want characterization tests for the
  Reappearance window first.

---

### PR 2 — Interface extraction + per-service directories (additive)

For each of the ~22 concrete services:
1. Add `IFoo.cs` next to `Foo.cs` declaring the public surface
2. Mark the concrete class `: IFoo`
3. Move both into `Foo/` subdirectory
4. Update construction sites — no behavior change yet, still
   `new Foo(...)`, but typed against `IFoo`

**Why this is safe**:
- Pure additive: no logic moves, no method bodies change
- Class is still concrete, still constructed by hand
- File moves are pure renames (csproj uses SDK-style globs — no
  per-file listing to update)
- Tests don't change (transport-level fakes stay)

**Regression guard**:
- Phase-0 tests green
- Build clean — extraction is correct only if every callsite still
  compiles against the interface
- Manual smoke: same as PR 1

**Verification**:
- For each interface, sanity-grep: every public method on the concrete
  has a matching member on the interface
- For each `*Service` type, callers should now reference `IXxxService`
  in field/parameter types

---

### PR 3 — DI bootstrap (medium risk; CAREFUL with construction order)

This is where regression risk to permissions / HID / topology spikes.

**Changes**:
- Add `Microsoft.Extensions.DependencyInjection` to App
- New `BoltMate.App/Composition/ServiceRegistration.cs` — single source
  of registrations
- `Program.Main` builds the provider, sets `App.ServiceProvider`
- `App.OnFrameworkInitializationCompleted` resolves from provider
  instead of `new`
- `_disposables` shrinks (container owns disposal of registered
  services)

**Key risks**:

1. **Construction order**: today's bootstrap calls
   `_topology.Start()`, `_mdnsTcp.Start()`, `_correlator = new(...)` in
   a specific sequence. DI is lazy by default — services materialise on
   first resolve. **If `Start()` runs before subscribers exist, events
   are missed.** Mitigation: keep an explicit composition-root method
   that resolves and starts each service in the same order as today.
   Don't let resolution order be implicit.

2. **Singleton vs transient lifetimes**: services that hold subscriptions
   (e.g. `TopologyCorrelator` subscribes to `_topology.Announcements`)
   MUST be singletons. Default `AddSingleton<T>()` everywhere unless
   there's a known reason for transient.

3. **`CompositeDisposable` vs container disposal**: container disposes
   in reverse registration order. Confirm reverse order matches today's
   manual dispose order in `App.Dispose`.

4. **Hot-path services**: `BoltReceiver`, `ReceiverManager`,
   `HidPpClient` are created per-receiver, not per-app. They stay
   `new()`-constructed inside `ReceiverManager` — DI registers the
   ManagerFactory, not the per-receiver objects.

**Regression guard**:
- Phase-0 tests green
- After bootstrap, log every resolved service + its lifetime — diff
  against today's startup log
- Manual smoke: full restart on both platforms, watch for missing
  notifications (compare log per-second event counts to a known-good
  baseline)
- **HID/topology smoke**: kb easy-switch back and forth, mouse follows,
  no missed link transitions, no missed announcement events
- **Permission smoke**: fresh install → welcome wizard fires; click
  through; tray badge updates correctly

---

### PR 4 — MVVM via ReactiveUI (UI-scope, lower stakes)

- Add `Avalonia.ReactiveUI` package
- For each window:
  - Extract `*ViewModel : ReactiveObject` (e.g. `SettingsViewModel`,
    `WelcomeViewModel`, `AppViewModel`)
  - Move state observables, command bindings, derived properties to VM
  - Window code-behind shrinks to ctor + `DataContext = vm` + native
    glue (activation policy P/Invoke, window placement, etc.)
- XAML rewritten with `{Binding ...}`; event handlers stay only where
  they MUST (e.g. `Closing` cancel-and-hide)
- VM unit tests — no UI thread, no Avalonia app fixture needed

**Why this is sequenced late despite being the biggest LOC change**:
- It's UI-only; doesn't touch HID, permissions, or topology services
  (which are already correctly typed as observables and consumed by VMs
  the same way they're consumed by code-behind today)
- User explicitly accepted UI regression risk

**Regression guard**:
- Phase-0 tests green
- New VM tests for: SettingsViewModel device-list filtering,
  WelcomeViewModel page navigation, AppViewModel tray-state derivation
- Manual UI smoke: open Settings tab, open Welcome, peer view updates,
  permission warnings appear/disappear
- **HID/topology/permissions code path is untouched** — but verify with
  log diff that no observable subscribers were lost in translation

---

### PR 5 — Core polish (medium risk; bundle careful changes)

Things that need test coverage before they're safe:

- `ConfigureAwait(false)` audit across `BoltMate.Core` (deferred from
  PR 1). Risk: changing sync-context continuation in time-sensitive
  topology code. Land only after Phase-0 round-trip tests cover the
  reappearance window.
- `TimeProvider` injection where wall-clock is read (~43 sites).
  Replace `DateTimeOffset.UtcNow` with `_timeProvider.GetUtcNow()`.
  Default: `TimeProvider.System` via DI. Tests use `FakeTimeProvider`.
- `[LoggerMessage]` source-gen for hot paths (HidPpClient.OnNotification,
  BoltReceiver.OnNotification dispatch, UDP per-announcement)
- `IAsyncDisposable` for network/HID transports
- Fire-and-forget helper: `ILogger.RunDetached(work, label)` that wraps
  `_ = Task.Run` with exception logging

**Regression guard**:
- Phase-0 tests green AND extended with time-mocked variants (replace
  real `Task.Delay` with FakeTimeProvider's advance)
- For each `ConfigureAwait(false)` site, verify the await wasn't
  feeding into UI-thread-sensitive code
- Manual smoke: full Mac+Win fan-out cycle, log diff

---

## Regression-prevention strategy (cross-cutting)

### Per-PR gate
Before any PR merges:
1. All Phase-0 characterization tests green
2. `dotnet test BoltMate.Tests/BoltMate.Tests.csproj` green (112+ tests)
3. Build clean on both `osx-arm64` and `win-x64` publish
4. Manual smoke test executed and logged:
   - Launch app on Mac
   - Launch app on Win
   - Press Easy-Switch on kb (Mac → Win), confirm mouse follows
   - Press Easy-Switch on kb (Win → Mac), confirm mouse follows
   - Open Settings, verify peer machine appears with correct device
     bindings
   - Open Settings → Network, verify all three transports report
     Healthy
5. Log diff vs prior session: same notification event types and
   approximate cadence

### Bisection-friendly commits
Within each PR, commits should be granular enough that
`git bisect` can pinpoint a regression to a single file's change.
No mass-rewrites in one commit.

### Snapshot baseline
Before PR 1, capture a "known-good" log and a screenshot of:
- Settings → Status tab
- Settings → Network tab
- Tray menu open
- Welcome wizard pages 1-4

Diff against equivalent captures after each PR. UI regressions are
acceptable; behavioral regressions (HID events missed, fan-out fails,
permissions misreported) are not.

### Test-first for any new abstraction
Interface extraction (PR 2): write the interface AND a test that
constructs the concrete via the interface BEFORE touching callsites.

DI bootstrap (PR 3): write a `ServiceProvider`-construction test that
resolves the full graph BEFORE wiring it into `App.OnFrameworkInitializationCompleted`.

MVVM (PR 4): VM tests before XAML rewires.

### Rollback plan
Each PR lands on a separate branch with no fast-forward — single merge
commit gives a clean revert if smoke testing finds an issue post-merge.
