# BoltMate refactor plan

Captured 2026-06-22. Findings from a critical pass over the codebase against
modern C# / .NET best practices and the desired architecture (MVVM via
ReactiveUI, DI/IoC via `IServiceCollection`/`IServiceProvider`, strong
interface design, one service per directory).

## Status legend
- ✅ Already compliant — no work needed
- ⚠️ Partial / mechanical fix
- ❌ Significant work / new pattern

---

## 1. Null comparisons — ✅ Already compliant

Zero `== null` / `!= null` instances anywhere. Codebase already uses
`is null` / `is not null` throughout.

## 2. Enum equality (`==`/`!=` → `is`/`is not`) — ⚠️ Mechanical (~25 sites)

Representative hits:
```
BatteryService.cs:118                    State == ChargingState.ChargeComplete
MdnsTcpChannel.cs:113,117,121,152,199    State == TransportState.X
TrayIconStatusController.cs:101,143,158  status == X, h.State == TransportState.Blocked
AppHealthService.cs:151,152              .State == TransportState.Blocked
UdpTopologyService.cs:136,563            _lastUdpState == state, OperationalStatus != Up
TrayMenuController.cs:62                 _permissionStatus == OverallStatus.AnyDenied
DeviceEnricher.cs:41                     change.Reason == ChangeReason.Add
NetworkPermission.cs:507                 OperationalStatus != Up
SettingsWindow.axaml.cs:502              res.Status == NetworkPermission.Status.Denied
PermissionsService.cs:206,215,226        .Status == NetworkPermission.Status.Granted
```

Note: `is`/`is not` doesn't compose into `&&` chains as readably as
`==`. Where the predicate combines two enum tests with `&&`, evaluate
whether `is` actually reads better case-by-case.

## 3. MVVM — ❌ Zero adoption

- No `ViewModels/` folder
- No `INotifyPropertyChanged` / `ReactiveObject` / `ViewModelBase`
- All three windows are pure code-behind:
  | File | Lines |
  |---|---|
  | `App.axaml.cs` | 481 |
  | `SettingsWindow.axaml.cs` | 612 |
  | `Welcome/WelcomeWindow.axaml.cs` | 564 |
- XAML binds directly to code-behind properties/events

**Approach**: ReactiveUI fits the project's existing Rx-first
architecture rule ([feedback-dotnet-reactive-style]). Each window grows
a paired `*ViewModel : ReactiveObject` that exposes `IObservable<T>` /
`ObservableAsPropertyHelper<T>` for state. Code-behind shrinks to
constructor + `DataContext` wiring + Avalonia-specific glue
(activation policy, window placement, native-handle callbacks).

## 4. DI/IoC — ❌ Zero adoption in main app

`IServiceProvider`/`IServiceCollection` exists only in
`BoltMate.LicenseApi` (ASP.NET project) and `BoltMate.Licensing`. Main
App + Core: zero.

`App.axaml.cs` does ~10 direct `new` constructions in bootstrap order:
```
new PermissionsService(_loggerFactory)
new ReceiverManager(transport, loggerFactory: _loggerFactory)
new SwitcherService(_manager, ...)
new UpdateService(_settings, ...)
new TrayMenuController(menu, _manager, _permissions!, ...)
new TrayIconStatusController(trays[0], _permissions!, ...)
new UdpTopologyService(_manager, _settings.Topology, machineId, ...)
new MdnsTcpChannel(_topology, _settings.Topology, machineId, ...)
new TopologyCorrelator(_manager, _switcher, ...)
new SettingsWindow(_manager, _settings, _permissions!, ...)
```

Lifecycle managed by hand via `CompositeDisposable`.

**Approach**: plain `ServiceCollection` in `Program.cs`, not
`Microsoft.Extensions.Hosting`. The host builder is overkill for a
tray-style Avalonia app — its value (config sources, hosted-service
lifecycle, graceful shutdown signals) doesn't apply here. Construction
order moves into `services.AddSingleton<T>()` calls and the provider
resolves it. Avalonia's `AppBuilder` doesn't natively know about
`IServiceProvider`, so build the provider in `Program.Main` before
`AppBuilder.Configure<App>()` and pass it into `App` (constructor
injection isn't supported by Avalonia's parameterless-app convention;
a static `App.ServiceProvider` field set from `Program.Main` is the
established pattern in Avalonia + DI samples).

## 5. Service interfaces — ⚠️ 1 of 14 has an interface

| Service | Interface | Location |
|---|---|---|
| `PermissionsService` | ✅ `IPermissionsService` | `App/Permissions/` |
| `BatteryService` | ❌ | `Core/HidPp/Features/` |
| `ChangeHostService` | ❌ | `Core/HidPp/Features/` |
| `DeviceFriendlyNameService` | ❌ | `Core/HidPp/Features/` |
| `DeviceInfoService` | ❌ | `Core/HidPp/Features/` |
| `DeviceNameService` | ❌ | `Core/HidPp/Features/` |
| `HostsInfoService` | ❌ | `Core/HidPp/Features/` |
| `ReprogControlsService` | ❌ | `Core/HidPp/Features/` |
| `RootService` | ❌ | `Core/HidPp/Features/` |
| `SwitcherService` | ❌ | `Core/Switcher/` |
| `UdpTopologyService` | ❌ | `Core/Topology/` |
| `AppHealthService` | ❌ | `App/Health/` |
| `PermissionStatusService` | ❌ | `App/` |
| `UpdateService` | ❌ | `App/Updates/` |

Plus service-shaped types without `*Service` suffix that also lack
interfaces: `MdnsTcpChannel`, `TopologyCorrelator`, `BoltReceiver`,
`ReceiverManager`, `DeviceEnricher`, `TrayMenuController`,
`TrayIconStatusController`, `HostBindingPersistence`, `HidPpClient`.

Tests today mock at the HID **transport** boundary
(`FakeReceiverTransport`/`FakeReceiverConnection`) rather than per-service —
that's actually robust and won't be displaced. Interface extraction is
additive: it unlocks per-service mocking AND enables DI.

## 6. One service per directory — ⚠️ Currently grouped

Today: `Core/HidPp/Features/` holds 8 service files flat. Same shape in
`Core/Topology/`, `App/Permissions/`, etc.

Ask: per-service directories with `IFoo.cs` + `Foo.cs` co-located:
```
Core/HidPp/Features/Battery/IBatteryService.cs + BatteryService.cs
Core/HidPp/Features/ChangeHost/IChangeHostService.cs + ChangeHostService.cs
...
```

~25 services × 2 files each. Pure organization gain — no functional
change. Will churn git blame across all moved files. Worth doing in
the same PR as interface extraction so file moves and interface
introduction land together.

---

## 7. Additional C# / .NET best-practice findings

### ✅ Already strong
- File-scoped namespaces: **56 of 57** .cs files
- No `.Result` / `.Wait()` deadlock patterns
- Records used heavily for DTOs/value objects: 25 records vs 53 classes
- Switch expressions favored: 16 expressions vs 2 statements
- `using` blocks nearly gone: only 2 remain (`MdnsTcpChannel.cs`,
  `TrayIconStatusController.cs`); rest are `using` declarations
- Only 1 `async void` and it's a documented event-handler continuation
- `IReadOnlyDictionary` / `IReadOnlyList` used for surfaces that should
  be read-only

### ⚠️ Inconsistent / opportunity

#### `ConfigureAwait(false)` — partial coverage in Core
Library code (`BoltMate.Core`) should always pass
`ConfigureAwait(false)` on await to avoid capturing the
synchronization context. Coverage today is uneven:

| File | Count |
|---|---|
| `BoltReceiver.cs` | 26 ✓ |
| `HostsInfoService.cs` | 7 ✓ |
| `DeviceFriendlyNameService.cs` | 5 ✓ |
| `ReprogControlsService.cs` | 5 ✓ |
| `DeviceNameService.cs` | 3 ✓ |
| `PairingBackup.cs` | 2 ✓ |
| `DeviceInfoService.cs` | 2 (others missing) |
| `BatteryService.cs` | 1 (others missing) |
| `ChangeHostService.cs` | 1 (others missing) |
| `RootService.cs` | 1 (others missing) |
| Other Core/Topology files | mostly 0 |

Add `ConfigureAwait(false)` everywhere in `BoltMate.Core` (library —
no UI context). Skip in `BoltMate.App` (Avalonia code-behind that
touches UI thread needs the captured context).

#### Logging: source-generated vs string-interpolation
**Zero `[LoggerMessage]` partial methods.** All logging today uses
`_logger.LogInformation("... {Foo}", value)` — fine at INF cadence
but ~3× slower than source-generated for hot paths. Hot paths in this
codebase:
- `HidPpClient.OnNotification` (every inbound frame)
- `BoltReceiver.OnNotification` dispatch (same)
- `UdpTopologyService` per-announcement logs (one per 2 s × peers)

Source-generated would shave allocation on those. Low priority — only
matters if logging shows up in flame graphs.

#### `TimeProvider` for testability
**43 direct `DateTime.UtcNow` / `DateTimeOffset.UtcNow` calls**. Means
time-sensitive logic (`TopologyCorrelator._pendingLost` reappearance
window, transport-health timers, retry backoffs) can't be unit-tested
deterministically. Inject `TimeProvider` (built-in since .NET 8) into
services that read wall-clock; tests pass `FakeTimeProvider` from
`Microsoft.Extensions.TimeProvider.Testing`.

#### `ArgumentNullException.ThrowIfNull` at public boundaries
Only 7 instances across the codebase (most in `Licensing`). Public
service constructors and external-input methods generally don't
validate nullable args. Add at public boundaries for fail-fast:
```csharp
public SwitcherService(ReceiverManager manager, ILogger<SwitcherService>? logger = null)
{
    ArgumentNullException.ThrowIfNull(manager);
    ...
}
```

#### `Program.cs` consistency
`BoltMate.Cli/Program.cs` uses **top-level statements** (modern).
`BoltMate.App/Program.cs` uses the old `class Program { static void Main(...) }`
pattern. Normalise to top-level statements in both — saves boilerplate
and is the C# 10+ default.

#### Primary constructors (C# 12)
No primary constructors used yet. Many services have boilerplate like:
```csharp
private readonly ReceiverManager _manager;
private readonly ILogger _logger;
public SwitcherService(ReceiverManager manager, ILogger logger) {
    _manager = manager; _logger = logger;
}
```

C# 12 primary constructors collapse this:
```csharp
public sealed class SwitcherService(ReceiverManager manager, ILogger<SwitcherService> logger)
{
    // manager + logger usable directly
}
```

Worth applying selectively where the constructor is purely
assigning-to-fields. Don't force it where the constructor has logic
(subject wiring, disposable composition, etc.).

#### Fire-and-forget tasks
`SwitcherService` uses `_ = Task.Run(async () => ...)` for the
verify-and-retry dance. The discarded task silently swallows
exceptions. Two safer patterns:

```csharp
// Option A: explicit handler
_ = Task.Run(VerifyAndRetryAsync).ContinueWith(
    t => _logger.LogError(t.Exception, "Verify-retry crashed"),
    TaskContinuationOptions.OnlyOnFaulted);

// Option B: wrap in helper
private void RunDetached(Func<Task> work, string label) =>
    _ = Task.Run(async () => {
        try { await work(); }
        catch (Exception ex) { _logger.LogError(ex, "{Label} crashed", label); }
    });
```

Worth a small helper extension on `ILogger`.

#### `IAsyncDisposable` for network / native handles
Today: `UdpTopologyService`, `MdnsTcpChannel`, `BoltReceiver`,
`WinReceiverConnection` all implement `IDisposable` synchronously
even though shutdown involves async work (`Task.Wait` on cancellation,
socket draining). Implementing `IAsyncDisposable` and using
`await using` at the bootstrap site would surface the async shutdown
cleanly. Avalonia desktop apps usually call `Dispose` at app-exit
which can tolerate sync shutdown — low priority unless a graceful
shutdown becomes important.

#### `using` blocks → declarations (2 remaining)
- `MdnsTcpChannel.cs`
- `TrayIconStatusController.cs`

Convert when touched.

---

## Proposed PR sequencing

Ordered by lift × payoff:

### PR 1 — Modernization sweep (small)
- Enum `==`/`!=` → `is`/`is not` (case-by-case for readability)
- `ConfigureAwait(false)` audit across `BoltMate.Core`
- `BoltMate.App/Program.cs` → top-level statements
- Remaining `using` blocks → declarations
- `ArgumentNullException.ThrowIfNull` at public constructors

### PR 2 — Interface extraction + per-service directories (medium)
- Extract `IXxxService` for every concrete service
- Move each service into its own directory with `IXxx.cs` + `Xxx.cs`
- Update construction sites to depend on the interface
- Tests: minimal churn — transport-level fakes stay; per-service
  mocking unlocks for new tests

### PR 3 — DI bootstrap (medium)
- Add `Microsoft.Extensions.DependencyInjection` package to App
- New `BoltMate.App/Composition/ServiceRegistration.cs` extension
- `Program.Main` builds the provider, sets `App.ServiceProvider`
- `App.OnFrameworkInitializationCompleted` resolves from provider
  instead of `new`
- `_disposables` shrinks (container handles disposal)
- Inject `TimeProvider` (default `TimeProvider.System`) where wall-clock
  is read

### PR 4 — MVVM via ReactiveUI (large)
- Add `Avalonia.ReactiveUI` + `ReactiveUI.Validation` packages
- For each window: extract `*ViewModel : ReactiveObject`
  - Move state observables, command bindings, validation, derived
    properties
  - Window code-behind shrinks to ctor + `DataContext = vm` + native
    glue
- XAML rewritten with `{Binding ...}` and `{x:Static}` markers; native
  event handlers stay where they MUST (e.g. `Closing` cancel-and-hide)
- Tests for VMs become straightforward — no UI thread, no Avalonia
  app fixture needed

### PR 5 — Logging / TimeProvider polish (small, optional)
- `[LoggerMessage]` source-gen for the 3-5 hottest log entries
- Inject `TimeProvider` into the dozen services that touch wall-clock
- Primary constructors where they don't lose clarity
